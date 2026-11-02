using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Slopsoil.Discord.Dave;
using Slopsoil.Discord.Rtp;
using Slopsoil.Discord.Transport;

namespace Slopsoil.Discord.Voice;

/// <summary>Where and as whom a voice websocket identifies (from VOICE_SERVER_UPDATE or STREAM_*).</summary>
/// <param name="Endpoint">Voice server host (with or without <c>wss://</c>).</param>
/// <param name="ServerId">Guild id for voice, <c>rtc_server_id</c> for go-live.</param>
/// <param name="DaveChannelId">DAVE MLS group id: the voice channel id, or <c>rtc_server_id - 1</c> for go-live.</param>
public sealed record VoiceServerInfo(
    string Endpoint, ulong ServerId, ulong UserId, string SessionId, string Token, ulong DaveChannelId);

/// <summary>
/// One voice-gateway v8 websocket + UDP media socket: the handshake and DAVE handling shared by
/// <see cref="VoiceConnection"/> and <see cref="GoLive.GoLiveConnection"/>. Port of discord.py-self's
/// <c>DiscordVoiceWebSocket</c> / <c>VoiceConnectionState</c> with the <c>video_compat.py</c> and
/// <c>davey_compat.py</c> patches applied.
/// </summary>
public sealed class VoiceSession : IAsyncDisposable
{
    /// <summary>Transport encryption modes we can send, most preferred first.</summary>
    public static readonly IReadOnlyList<string> SupportedModes = [TransportCrypto.XChaChaRtpSize, TransportCrypto.XSalsaLite];

    public const int OpusPayloadType = 120;
    public const int H264PayloadType = 101;
    public const int RtxPayloadType = 102;
    public const uint VideoSsrcOffset = 1;
    public const uint RtxSsrcOffset = 2;
    public const int DiscoveryPacketLength = 74;

    private readonly VoiceServerInfo _info;
    private readonly VoiceOptions _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, int> _pendingTransitions = [];
    private readonly HashSet<ulong> _connectedUsers = [];
    private readonly Lock _usersLock = new();
    private IWebSocketConnection? _ws;
    private IUdpConnection? _udp;
    private Task _loop = Task.CompletedTask;
    private Task _heartbeat = Task.CompletedTask;
    private int _seqAck = -1;
    private int _disposed;
    private bool _daveDowngraded;

    private VoiceSession(VoiceServerInfo info, VoiceOptions options)
    {
        _info = info;
        _options = options;
    }

    /// <summary>Audio SSRC (READY).</summary>
    public uint Ssrc { get; private set; }

    /// <summary>Video SSRC — always audio + 1 (<c>video_compat.VIDEO_SSRC_OFFSET</c>).</summary>
    public uint VideoSsrc => Ssrc + VideoSsrcOffset;

    public uint RtxSsrc => Ssrc + RtxSsrcOffset;

    /// <summary>Negotiated transport encryption mode (SESSION_DESCRIPTION).</summary>
    public string Mode { get; private set; } = "";

    public byte[] SecretKey { get; private set; } = [];

    /// <summary>Our address as seen by the voice server (UDP IP discovery).</summary>
    public string ExternalIp { get; private set; } = "";

    public int ExternalPort { get; private set; }

    public IDaveSession? DaveSession { get; private set; }

    public int DaveProtocolVersion { get; private set; }

    /// <summary>True when media must be DAVE-encrypted (discord.py-self <c>can_encrypt</c>).</summary>
    public bool CanEncrypt => DaveProtocolVersion != 0 && DaveSession is { Ready: true };

    /// <summary>Last sequence number received from the server, echoed in heartbeats.</summary>
    public int SeqAck => Volatile.Read(ref _seqAck);

    /// <summary>Close code if the server closed the websocket.</summary>
    public int? CloseCode { get; private set; }

    /// <summary>Completes when the websocket receive loop ends (closed, failed or disposed). Never faults.</summary>
    public Task Completion => _loop;

    public VoiceServerInfo Info => _info;

    /// <summary>Users the voice server reported in the call (ops 11/13) — DAVE's recognised users besides us.</summary>
    public IReadOnlyCollection<ulong> ConnectedUserIds
    {
        get
        {
            lock (_usersLock)
            {
                return [.. _connectedUsers];
            }
        }
    }

    /// <summary>Open the websocket, IDENTIFY, and complete the handshake through SESSION_DESCRIPTION.</summary>
    public static async Task<VoiceSession> ConnectAsync(VoiceServerInfo info, VoiceOptions options, CancellationToken ct)
    {
        var session = new VoiceSession(info, options);
        try
        {
            await session.StartAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Send one (already encrypted) RTP packet. False if dropped or the socket is gone.</summary>
    public bool SendPacket(ReadOnlySpan<byte> packet)
    {
        var udp = _udp;
        if (udp is null || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            return udp.Send(packet);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            return false;
        }
    }

    /// <summary>Op 5 SPEAKING (flags: 1 = microphone, 2 = soundshare, 4 = priority).</summary>
    public Task SetSpeakingAsync(int flags, CancellationToken ct) =>
        SendJsonAsync(VoiceOp.Speaking, new JsonObject { ["speaking"] = flags, ["delay"] = 0, ["ssrc"] = Ssrc }, ct);

    /// <summary>Op 12 VIDEO with video/rtx SSRCs and the stream limits (<c>video_compat._patched_client_connect</c>).</summary>
    public Task AnnounceVideoAsync(CancellationToken ct) =>
        SendJsonAsync(
            VoiceOp.Video,
            new JsonObject
            {
                ["audio_ssrc"] = Ssrc,
                ["video_ssrc"] = VideoSsrc,
                ["rtx_ssrc"] = RtxSsrc,
                ["streams"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "video",
                        ["rid"] = "100",
                        ["ssrc"] = VideoSsrc,
                        ["active"] = true,
                        ["quality"] = 100,
                        ["rtx_ssrc"] = RtxSsrc,
                        ["max_bitrate"] = 10_000_000,
                        ["max_framerate"] = 60,
                        ["max_resolution"] = new JsonObject { ["type"] = "fixed", ["width"] = 1920, ["height"] = 1080 },
                    },
                },
            },
            ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ws is { } ws)
        {
            try
            {
                using var close = new CancellationTokenSource(_options.CloseTimeout, _options.Time);
                await ws.CloseAsync(1000, close.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"voice ws close failed: {ex.Message}");
            }
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        await _heartbeat.ConfigureAwait(false); // never faults: the loop catches everything

        if (_ws is not null)
        {
            await _ws.DisposeAsync().ConfigureAwait(false);
        }

        _udp?.Dispose();
        DaveSession?.Dispose();
        _handshake.TrySetCanceled();
        _cts.Dispose();
        _sendLock.Dispose();
    }

    // ── handshake ─────────────────────────────────────────────────────────────

    private async Task StartAsync(CancellationToken ct)
    {
        _ws = _options.WebSockets.Create();
        var endpoint = VoiceJson.StripScheme(_info.Endpoint);
        await _ws.ConnectAsync(new Uri($"wss://{endpoint}/?v=8"), ct).ConfigureAwait(false);
        await SendJsonAsync(VoiceOp.Identify, IdentifyPayload(), ct).ConfigureAwait(false);
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        await _handshake.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    internal JsonObject IdentifyPayload() => new()
    {
        ["server_id"] = VoiceJson.Id(_info.ServerId),
        ["user_id"] = VoiceJson.Id(_info.UserId),
        ["session_id"] = _info.SessionId,
        ["token"] = _info.Token,
        ["max_dave_protocol_version"] = _options.EffectiveMaxDaveProtocolVersion,
        ["video"] = true,
        ["streams"] = new JsonArray { new JsonObject { ["type"] = "video", ["rid"] = "100", ["quality"] = 100 } },
    };

    internal static JsonObject SelectProtocolPayload(string ip, int port, string mode) => new()
    {
        ["protocol"] = "udp",
        ["data"] = new JsonObject { ["address"] = ip, ["port"] = port, ["mode"] = mode },
        ["codecs"] = new JsonArray
        {
            new JsonObject { ["name"] = "opus", ["type"] = "audio", ["priority"] = 1000, ["payload_type"] = OpusPayloadType },
            new JsonObject
            {
                ["name"] = "H264",
                ["type"] = "video",
                ["priority"] = 1000,
                ["payload_type"] = H264PayloadType,
                ["rtx_payload_type"] = RtxPayloadType,
            },
        },
    };

    /// <summary>First mode from <see cref="SupportedModes"/> the server offers, or null.</summary>
    public static string? ChooseMode(IEnumerable<string> offered)
    {
        var set = offered.ToHashSet(StringComparer.Ordinal);
        return SupportedModes.FirstOrDefault(set.Contains);
    }

    /// <summary>74-byte IP discovery request: type 1, length 70, SSRC, zero padding.</summary>
    public static byte[] BuildDiscoveryRequest(uint ssrc)
    {
        var packet = new byte[DiscoveryPacketLength];
        BinaryPrimitives.WriteUInt16BigEndian(packet, 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 70);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        return packet;
    }

    /// <summary>Parse a discovery response (byte 1 == 2, 74 bytes): NUL-terminated ASCII IP at 8, BE port at the end.</summary>
    public static bool TryParseDiscoveryResponse(ReadOnlySpan<byte> packet, out string ip, out int port)
    {
        ip = "";
        port = 0;
        if (packet.Length != DiscoveryPacketLength || packet[1] != 0x02)
        {
            return false;
        }

        var field = packet[8..^2];
        var end = field.IndexOf((byte)0);
        if (end < 0)
        {
            return false;
        }

        ip = Encoding.ASCII.GetString(field[..end]);
        port = BinaryPrimitives.ReadUInt16BigEndian(packet[^2..]);
        return true;
    }

    // ── receive loop ──────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await _ws!.ReceiveAsync(ct).ConfigureAwait(false);
                if (frame.IsClose)
                {
                    CloseCode = frame.CloseCode;
                    Log($"voice ws closed by server ({frame.CloseCode})");
                    _handshake.TrySetException(new VoiceConnectionException(
                        $"voice websocket closed ({frame.CloseCode})", frame.CloseCode));
                    return;
                }

                if (frame.IsText)
                {
                    await HandleTextAsync(frame.Data, ct).ConfigureAwait(false);
                }
                else
                {
                    await HandleBinaryAsync(frame.Data, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log($"voice ws loop ended: {ex.Message}");
            _handshake.TrySetException(new VoiceConnectionException("voice handshake failed: " + ex.Message, null, ex));
        }
        finally
        {
            _handshake.TrySetException(new VoiceConnectionException("voice websocket ended"));
        }
    }

    private async Task HandleTextAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        var op = root.GetProperty("op").GetInt32();
        var d = root.TryGetProperty("d", out var dv) ? dv : default;
        if (root.TryGetProperty("seq", out var seq) && seq.TryGetInt32(out var s))
        {
            Volatile.Write(ref _seqAck, s);
        }

        switch (op)
        {
            case VoiceOp.Ready:
                await OnReadyAsync(d, ct).ConfigureAwait(false);
                break;
            case VoiceOp.SessionDescription:
                await OnSessionDescriptionAsync(d, ct).ConfigureAwait(false);
                _handshake.TrySetResult();
                break;
            case VoiceOp.Hello:
                var interval = TimeSpan.FromMilliseconds(d.GetProperty("heartbeat_interval").GetDouble());
                _heartbeat = Task.Run(() => HeartbeatLoopAsync(interval, ct), CancellationToken.None);
                break;
            case VoiceOp.HeartbeatAck:
            case VoiceOp.Resumed:
            case VoiceOp.VoiceBackendVersion:
                break;
            case VoiceOp.ClientsConnect:
                OnClientsConnect(d);
                break;
            case VoiceOp.ClientDisconnect:
                if (VoiceJson.Snowflake(d, "user_id") is { } gone)
                {
                    lock (_usersLock)
                    {
                        _connectedUsers.Remove(gone);
                    }
                }

                break;
            default:
                if (DaveSession is not null)
                {
                    await HandleDaveTextAsync(op, d, ct).ConfigureAwait(false);
                }

                break;
        }
    }

    private void OnClientsConnect(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("user_ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        lock (_usersLock)
        {
            foreach (var id in ids.EnumerateArray())
            {
                if (ulong.TryParse(id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText(), out var uid))
                {
                    _connectedUsers.Add(uid);
                }
            }
        }
    }

    private async Task OnReadyAsync(JsonElement d, CancellationToken ct)
    {
        Ssrc = d.GetProperty("ssrc").GetUInt32();
        var ip = d.GetProperty("ip").GetString()!;
        var port = d.GetProperty("port").GetInt32();
        var modes = d.GetProperty("modes").EnumerateArray().Select(m => m.GetString() ?? "").ToList();

        await SendJsonAsync(VoiceOp.VoiceBackendVersion, new JsonObject(), ct).ConfigureAwait(false);

        _udp = _options.Udp.Create();
        _udp.Connect(ip, port);
        (ExternalIp, ExternalPort) = await DiscoverIpAsync(_udp, Ssrc, ct).ConfigureAwait(false);

        var mode = ChooseMode(modes)
            ?? throw new VoiceConnectionException($"no supported encryption mode in [{string.Join(", ", modes)}]");
        Log($"voice READY ssrc={Ssrc} mode={mode}");
        await SendJsonAsync(VoiceOp.SelectProtocol, SelectProtocolPayload(ExternalIp, ExternalPort, mode), ct)
            .ConfigureAwait(false);
    }

    private static async Task<(string Ip, int Port)> DiscoverIpAsync(IUdpConnection udp, uint ssrc, CancellationToken ct)
    {
        udp.Send(BuildDiscoveryRequest(ssrc));
        while (true)
        {
            var reply = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            if (TryParseDiscoveryResponse(reply, out var ip, out var port))
            {
                return (ip, port);
            }
        }
    }

    private async Task OnSessionDescriptionAsync(JsonElement d, CancellationToken ct)
    {
        Mode = d.GetProperty("mode").GetString()!;
        SecretKey = [.. d.GetProperty("secret_key").EnumerateArray().Select(b => b.GetByte())];

        // "Not speaking" tells the server our SSRC before any media (discord.py-self load_secret_key).
        await SetSpeakingAsync(0, ct).ConfigureAwait(false);

        DaveProtocolVersion = VoiceJson.Int(d, "dave_protocol_version");
        if (DaveProtocolVersion > 0)
        {
            await ReinitDaveSessionAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task HeartbeatLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, _options.Time, ct).ConfigureAwait(false);
                await SendJsonAsync(
                    VoiceOp.Heartbeat,
                    new JsonObject { ["t"] = _options.Time.GetUtcNow().ToUnixTimeMilliseconds(), ["seq_ack"] = SeqAck },
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"voice heartbeat stopped: {ex.Message}");
        }
    }

    // ── DAVE ──────────────────────────────────────────────────────────────────

    private async Task HandleDaveTextAsync(int op, JsonElement d, CancellationToken ct)
    {
        switch (op)
        {
            case VoiceOp.DavePrepareTransition:
            {
                var transitionId = VoiceJson.Int(d, "transition_id");
                var version = VoiceJson.Int(d, "protocol_version");
                _pendingTransitions[transitionId] = version;
                if (transitionId == 0)
                {
                    ExecuteTransition(transitionId);
                }
                else
                {
                    if (version == 0)
                    {
                        DaveSession?.SetPassthroughMode(true);
                    }

                    await SendTransitionReadyAsync(transitionId, ct).ConfigureAwait(false);
                }

                break;
            }

            case VoiceOp.DaveExecuteTransition:
                ExecuteTransition(VoiceJson.Int(d, "transition_id"));
                break;

            case VoiceOp.DavePrepareEpoch:
                // Epoch 1 means a new MLS group is created for the given protocol version.
                if (VoiceJson.Int(d, "epoch") == 1)
                {
                    DaveProtocolVersion = VoiceJson.Int(d, "protocol_version");
                    await ReinitDaveSessionAsync(ct).ConfigureAwait(false);
                }

                break;
        }
    }

    private async Task HandleBinaryAsync(ReadOnlyMemory<byte> memory, CancellationToken ct)
    {
        if (memory.Length < 3)
        {
            return;
        }

        Volatile.Write(ref _seqAck, BinaryPrimitives.ReadUInt16BigEndian(memory.Span));
        var op = memory.Span[2];
        var dave = DaveSession;
        if (dave is null)
        {
            return;
        }

        switch (op)
        {
            case VoiceOp.MlsExternalSender:
                dave.SetExternalSender(memory.Span[3..]);
                break;

            case VoiceOp.MlsProposals when memory.Length >= 4:
            {
                var operation = memory.Span[3] == 0 ? DaveProposalsOperation.Append : DaveProposalsOperation.Revoke;
                byte[]? commitWelcome;
                try
                {
                    commitWelcome = dave.ProcessProposals(operation, memory.Span[4..], RecognizedUsers());
                }
                catch (Exception ex)
                {
                    // davey_compat: a failed process_proposals logs and sends nothing.
                    Log($"DAVE process_proposals failed: {ex.Message}");
                    commitWelcome = null;
                }

                if (commitWelcome is not null)
                {
                    await SendBinaryAsync(VoiceOp.MlsCommitWelcome, commitWelcome, ct).ConfigureAwait(false);
                }

                break;
            }

            case VoiceOp.MlsAnnounceCommitTransition when memory.Length >= 5:
            case VoiceOp.MlsWelcome when memory.Length >= 5:
            {
                var transitionId = BinaryPrimitives.ReadUInt16BigEndian(memory.Span[3..]);
                bool ok;
                try
                {
                    ok = op == VoiceOp.MlsWelcome
                        ? dave.ProcessWelcome(memory.Span[5..], RecognizedUsers())
                        : dave.ProcessCommit(memory.Span[5..]);
                }
                catch (Exception ex)
                {
                    Log($"DAVE {(op == VoiceOp.MlsWelcome ? "welcome" : "commit")} failed: {ex.Message}");
                    ok = false;
                }

                if (ok)
                {
                    if (transitionId != 0)
                    {
                        _pendingTransitions[transitionId] = DaveProtocolVersion;
                        await SendTransitionReadyAsync(transitionId, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    await RecoverFromInvalidCommitAsync(transitionId, ct).ConfigureAwait(false);
                }

                break;
            }
        }
    }

    private IReadOnlyCollection<ulong> RecognizedUsers()
    {
        lock (_usersLock)
        {
            return [_info.UserId, .. _connectedUsers.Where(u => u != _info.UserId)];
        }
    }

    private async Task ReinitDaveSessionAsync(CancellationToken ct)
    {
        if (DaveProtocolVersion > 0)
        {
            var factory = _options.DaveSessionFactory
                ?? throw new VoiceConnectionException("server requires DAVE but no DAVE session factory is configured");
            DaveSession ??= factory();
            DaveSession.Reinit(DaveProtocolVersion, _info.UserId, _info.DaveChannelId);
            await SendBinaryAsync(VoiceOp.MlsKeyPackage, DaveSession.GetSerializedKeyPackage(), ct).ConfigureAwait(false);
        }
        else if (DaveSession is not null)
        {
            DaveSession.Reset();
            DaveSession.SetPassthroughMode(true);
        }
    }

    private async Task RecoverFromInvalidCommitAsync(int transitionId, CancellationToken ct)
    {
        await SendJsonAsync(VoiceOp.MlsInvalidCommitWelcome, new JsonObject { ["transition_id"] = transitionId }, ct)
            .ConfigureAwait(false);
        await ReinitDaveSessionAsync(ct).ConfigureAwait(false);
    }

    private void ExecuteTransition(int transitionId)
    {
        if (!_pendingTransitions.Remove(transitionId, out var version))
        {
            Log($"DAVE execute transition {transitionId} without a pending transition");
            return;
        }

        var old = DaveProtocolVersion;
        DaveProtocolVersion = version;
        if (old != version && version == 0)
        {
            _daveDowngraded = true;
            Log("DAVE session downgraded");
        }
        else if (transitionId > 0 && _daveDowngraded)
        {
            _daveDowngraded = false;
            DaveSession?.SetPassthroughMode(true);
            Log("DAVE session upgraded");
        }
    }

    private Task SendTransitionReadyAsync(int transitionId, CancellationToken ct) =>
        SendJsonAsync(VoiceOp.DaveTransitionReady, new JsonObject { ["transition_id"] = transitionId }, ct);

    // ── sending ───────────────────────────────────────────────────────────────

    private async Task SendJsonAsync(int op, JsonNode data, CancellationToken ct)
    {
        var json = new JsonObject { ["op"] = op, ["d"] = data }.ToJsonString();
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws!.SendTextAsync(json, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Client binary frames are the opcode byte followed by the payload (no sequence number).</summary>
    private async Task SendBinaryAsync(int op, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[payload.Length + 1];
        frame[0] = (byte)op;
        payload.CopyTo(frame, 1);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws!.SendBinaryAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void Log(string message) => _options.Log?.Invoke(message);
}

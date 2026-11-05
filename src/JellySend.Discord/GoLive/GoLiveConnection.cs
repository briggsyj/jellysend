using System.Text.Json;
using System.Text.Json.Nodes;
using JellySend.Discord.Dave;
using JellySend.Discord.Gateway;
using JellySend.Discord.Voice;

namespace JellySend.Discord.GoLive;

/// <summary>
/// A Discord go-live (screenshare) connection — port of <c>streaming/golive.py</c> GoLiveConnection.
/// Gateway op 18 STREAM_CREATE → STREAM_CREATE + STREAM_SERVER_UPDATE dispatches → a second
/// <see cref="VoiceSession"/> to the stream server → op 12 VIDEO → op 22 STREAM_SET_PAUSED(false).
/// </summary>
public sealed class GoLiveConnection : IAsyncDisposable
{
    private readonly IGatewayClient _gateway;
    private readonly VoiceOptions _options;
    private readonly string _sessionId;
    private readonly Lock _keyLock = new();
    private VoiceSession? _session;
    private string? _streamKey;
    private int _disconnected;

    private GoLiveConnection(IGatewayClient gateway, ulong guildId, ulong channelId, string sessionId, VoiceOptions options)
    {
        _gateway = gateway;
        GuildId = guildId;
        ChannelId = channelId;
        _sessionId = sessionId;
        _options = options;
    }

    public ulong GuildId { get; }

    public ulong ChannelId { get; }

    /// <summary><c>rtc_server_id</c> from STREAM_CREATE (the stream's voice server id).</summary>
    public ulong ServerId { get; private set; }

    /// <summary>Active stream key; null once STREAM_DELETE has been sent.</summary>
    public string? StreamKey
    {
        get
        {
            lock (_keyLock)
            {
                return _streamKey;
            }
        }
    }

    public VoiceSession Session => _session ?? throw new InvalidOperationException("go-live connection is not established");

    public uint Ssrc => Session.Ssrc;

    public uint VideoSsrc => Session.VideoSsrc;

    public string Mode => Session.Mode;

    public byte[] SecretKey => Session.SecretKey;

    public IDaveSession? DaveSession => Session.DaveSession;

    public bool CanEncrypt => Session.CanEncrypt;

    public bool SendPacket(ReadOnlySpan<byte> packet) => _session?.SendPacket(packet) ?? false;

    public static string MakeStreamKey(ulong guildId, ulong channelId, ulong userId) =>
        $"guild:{VoiceJson.Id(guildId)}:{VoiceJson.Id(channelId)}:{VoiceJson.Id(userId)}";

    /// <summary>Go live in <paramref name="voice"/>'s channel, identifying with its voice session id.</summary>
    public static Task<GoLiveConnection> ConnectAsync(
        IGatewayClient gateway, VoiceConnection voice, VoiceOptions options, CancellationToken ct = default) =>
        ConnectAsync(gateway, voice.GuildId, voice.ChannelId, voice.SessionId, options, ct);

    /// <summary>
    /// Start a go-live stream. On any failure (incl. <see cref="TimeoutException"/> after
    /// <see cref="VoiceOptions.ConnectTimeout"/>) the partial state is torn down — STREAM_DELETE included
    /// once op 18 went out — before the exception propagates.
    /// </summary>
    public static async Task<GoLiveConnection> ConnectAsync(
        IGatewayClient gateway, ulong guildId, ulong channelId, string sessionId, VoiceOptions options,
        CancellationToken ct = default)
    {
        var connection = new GoLiveConnection(gateway, guildId, channelId, sessionId, options);
        using var timeout = new CancellationTokenSource(options.ConnectTimeout, options.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await connection.ConnectCoreAsync(linked.Token).ConfigureAwait(false);
            return connection;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await connection.DisconnectAsync().ConfigureAwait(false);
            throw new TimeoutException($"go-live connect timed out after {options.ConnectTimeout}");
        }
        catch
        {
            await connection.DisconnectAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        var userId = _gateway.UserId;
        var streamKey = MakeStreamKey(GuildId, ChannelId, userId);
        var created = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new TaskCompletionSource<(string Token, string Endpoint)>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnDispatch(string type, JsonElement d)
        {
            if (VoiceJson.String(d, "stream_key") != streamKey)
            {
                return;
            }

            if (type == "STREAM_CREATE" && VoiceJson.Snowflake(d, "rtc_server_id") is { } rtcServerId)
            {
                created.TrySetResult(rtcServerId);
            }
            else if (type == "STREAM_SERVER_UPDATE"
                && VoiceJson.String(d, "token") is { } token
                && VoiceJson.String(d, "endpoint") is { } endpoint)
            {
                server.TrySetResult((token, VoiceJson.StripScheme(endpoint)));
            }
        }

        // Listen before sending op 18 so dispatches that arrive immediately are not lost.
        _gateway.Dispatch += OnDispatch;
        try
        {
            lock (_keyLock)
            {
                _streamKey = streamKey;
            }

            _options.Log?.Invoke($"go-live: STREAM_CREATE guild={GuildId} channel={ChannelId}");
            await _gateway.SendAsync(
                GatewayOp.StreamCreate,
                new JsonObject
                {
                    ["type"] = "guild",
                    ["guild_id"] = VoiceJson.Id(GuildId),
                    ["channel_id"] = VoiceJson.Id(ChannelId),
                    ["preferred_region"] = null,
                },
                ct).ConfigureAwait(false);

            // Op 22 right after op 18, as golive.py does: marks the stream active before the stream server handshake.
            await _gateway.SendAsync(
                GatewayOp.StreamSetPaused,
                new JsonObject { ["stream_key"] = streamKey, ["paused"] = false },
                ct).ConfigureAwait(false);

            ServerId = await created.Task.WaitAsync(ct).ConfigureAwait(false);
            var (token, endpoint) = await server.Task.WaitAsync(ct).ConfigureAwait(false);
            _options.Log?.Invoke($"go-live: stream server {endpoint} (server_id={ServerId})");

            // The go-live DAVE group id is rtc_server_id - 1 (golive.py reinit_dave_session).
            var info = new VoiceServerInfo(endpoint, ServerId, userId, _sessionId, token, ServerId - 1)
            {
                Label = "stream",
                // Viewers of the stream are the members of the voice channel it is in.
                ChannelMembers = _options.ChannelMembers is { } members ? () => members(GuildId, ChannelId) : null,
            };
            _session = await VoiceSession.ConnectAsync(info, _options, ct).ConfigureAwait(false);
            await _session.AnnounceVideoAsync(ct).ConfigureAwait(false);

        }
        finally
        {
            _gateway.Dispatch -= OnDispatch;
        }
    }

    /// <summary>
    /// Stop the stream and release everything. Idempotent: STREAM_DELETE is sent at most once, since a
    /// repeated delete for the same user/channel key would end a newer go-live started in the meantime.
    /// </summary>
    public async Task DisconnectAsync()
    {
        string? streamKey;
        lock (_keyLock)
        {
            (streamKey, _streamKey) = (_streamKey, null);
        }

        if (streamKey is not null)
        {
            try
            {
                await _gateway.SendAsync(
                    GatewayOp.StreamDelete, new JsonObject { ["stream_key"] = streamKey }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"go-live: could not send STREAM_DELETE: {ex.Message}");
            }
        }

        if (Interlocked.Exchange(ref _disconnected, 1) == 0 && _session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}

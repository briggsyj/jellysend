using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Slopsoil.Discord.Dave;
using Slopsoil.Discord.Gateway;
using Slopsoil.Discord.Transport;
using Slopsoil.Discord.Voice;

namespace Slopsoil.Tests.Discord;

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
}

/// <summary>Gateway fake: records sends; <see cref="OnSend"/> can answer with dispatches.</summary>
internal sealed class FakeGateway : IGatewayClient
{
    public ulong UserId { get; init; } = 42;

    public string SessionId { get; init; } = "main-session";

    public List<(int Op, JsonElement Data)> Sent { get; } = [];

    public Action<FakeGateway, int, JsonElement>? OnSend { get; set; }

    public Exception? FailSend { get; set; }

    public event Action<string, JsonElement>? Dispatch;

    public Task SendAsync(int op, object? data, CancellationToken ct)
    {
        if (FailSend is not null)
        {
            throw FailSend;
        }

        var element = JsonSerializer.SerializeToElement(data);
        lock (Sent)
        {
            Sent.Add((op, element));
        }

        OnSend?.Invoke(this, op, element);
        return Task.CompletedTask;
    }

    public void Raise(string type, string json)
    {
        using var doc = JsonDocument.Parse(json);
        Dispatch?.Invoke(type, doc.RootElement);
    }

    public int Subscribers => Dispatch?.GetInvocationList().Length ?? 0;

    public List<int> Ops()
    {
        lock (Sent)
        {
            return [.. Sent.Select(s => s.Op)];
        }
    }
}

/// <summary>Websocket fake: scripted server frames in, recorded client frames out.</summary>
internal sealed class FakeWebSocket : IWebSocketConnection
{
    private readonly Channel<WebSocketFrame> _inbound = Channel.CreateUnbounded<WebSocketFrame>();
    private readonly Channel<WebSocketFrame> _outbound = Channel.CreateUnbounded<WebSocketFrame>();

    public Uri? Uri { get; private set; }

    public int? ClosedWith { get; private set; }

    public bool Disposed { get; private set; }

    public bool ThrowOnClose { get; set; }

    /// <summary>Called for every client text frame (op, d) — lets a test answer like a server.</summary>
    public Action<FakeWebSocket, int, JsonElement>? OnText { get; set; }

    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        Uri = uri;
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string json, CancellationToken ct)
    {
        _outbound.Writer.TryWrite(new WebSocketFrame(true, Encoding.UTF8.GetBytes(json)));
        if (OnText is not null)
        {
            using var doc = JsonDocument.Parse(json);
            OnText(this, doc.RootElement.GetProperty("op").GetInt32(), doc.RootElement.GetProperty("d"));
        }

        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        _outbound.Writer.TryWrite(new WebSocketFrame(false, data.ToArray()));
        return Task.CompletedTask;
    }

    public async Task<WebSocketFrame> ReceiveAsync(CancellationToken ct) => await _inbound.Reader.ReadAsync(ct);

    public Task CloseAsync(int code, CancellationToken ct)
    {
        ClosedWith = code;
        return ThrowOnClose ? throw new InvalidOperationException("close failed") : Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    // ── server side ──

    public void Push(int op, string d, int? seq = null) =>
        _inbound.Writer.TryWrite(new WebSocketFrame(
            true, Encoding.UTF8.GetBytes(seq is { } s ? $"{{\"op\":{op},\"d\":{d},\"seq\":{s}}}" : $"{{\"op\":{op},\"d\":{d}}}")));

    public void PushRaw(string json) => _inbound.Writer.TryWrite(new WebSocketFrame(true, Encoding.UTF8.GetBytes(json)));

    /// <summary>Server binary frame: 2-byte BE seq, opcode, payload.</summary>
    public void PushBinary(ushort seq, int op, params byte[] payload)
    {
        var frame = new byte[3 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, seq);
        frame[2] = (byte)op;
        payload.CopyTo(frame, 3);
        _inbound.Writer.TryWrite(new WebSocketFrame(false, frame));
    }

    public void PushBytes(params byte[] frame) => _inbound.Writer.TryWrite(new WebSocketFrame(false, frame));

    public void PushClose(int code) => _inbound.Writer.TryWrite(new WebSocketFrame(false, default, true, code));

    public void Fail(Exception ex) => _inbound.Writer.TryComplete(ex);

    /// <summary>Next client frame (text or binary).</summary>
    public async Task<WebSocketFrame> NextAsync()
    {
        using var cts = new CancellationTokenSource(Wait.Timeout);
        return await _outbound.Reader.ReadAsync(cts.Token);
    }

    /// <summary>Skip client frames until a text frame with <paramref name="op"/>; returns its "d".</summary>
    public async Task<JsonElement> NextTextAsync(int op)
    {
        while (true)
        {
            var frame = await NextAsync();
            if (!frame.IsText)
            {
                continue;
            }

            var root = JsonDocument.Parse(frame.Text).RootElement;
            if (root.GetProperty("op").GetInt32() == op)
            {
                return root.GetProperty("d");
            }
        }
    }

    /// <summary>Skip client frames until a binary frame; returns it whole (opcode byte first).</summary>
    public async Task<byte[]> NextBinaryAsync()
    {
        while (true)
        {
            var frame = await NextAsync();
            if (!frame.IsText)
            {
                return frame.Data.ToArray();
            }
        }
    }

    /// <summary>Drain everything sent so far.</summary>
    public List<WebSocketFrame> Drain()
    {
        var list = new List<WebSocketFrame>();
        while (_outbound.Reader.TryRead(out var f))
        {
            list.Add(f);
        }

        return list;
    }
}

internal sealed class FakeWebSocketFactory(params FakeWebSocket[] sockets) : IWebSocketFactory
{
    private readonly Queue<FakeWebSocket> _sockets = new(sockets);

    public List<FakeWebSocket> Created { get; } = [];

    public IWebSocketConnection Create()
    {
        var ws = _sockets.Count > 0 ? _sockets.Dequeue() : new FakeWebSocket();
        Created.Add(ws);
        return ws;
    }
}

/// <summary>UDP fake: answers IP discovery (after optional junk) with a scripted address.</summary>
internal sealed class FakeUdp : IUdpConnection
{
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();

    public string ReplyIp { get; init; } = "203.0.113.7";

    public int ReplyPort { get; init; } = 50004;

    public bool AnswerDiscovery { get; init; } = true;

    public bool SendJunkFirst { get; init; } = true;

    public Exception? ThrowOnSend { get; set; }

    public bool SendResult { get; set; } = true;

    public (string Host, int Port)? Connected { get; private set; }

    public List<byte[]> Sent { get; } = [];

    public bool Disposed { get; private set; }

    public void Connect(string host, int port) => Connected = (host, port);

    public bool Send(ReadOnlySpan<byte> datagram)
    {
        if (ThrowOnSend is not null && Sent.Count > 0)
        {
            throw ThrowOnSend;
        }

        var copy = datagram.ToArray();
        Sent.Add(copy);
        if (Sent.Count == 1 && AnswerDiscovery)
        {
            if (SendJunkFirst)
            {
                _inbound.Writer.TryWrite([0x80, 0x78, 0, 1]);
                var wrongType = new byte[74];
                wrongType[1] = 0x01;
                _inbound.Writer.TryWrite(wrongType);
            }

            _inbound.Writer.TryWrite(DiscoveryReply(ReplyIp, ReplyPort, BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(4))));
        }

        return SendResult;
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct) => await _inbound.Reader.ReadAsync(ct);

    public void Dispose() => Disposed = true;

    public static byte[] DiscoveryReply(string ip, int port, uint ssrc)
    {
        var reply = new byte[74];
        BinaryPrimitives.WriteUInt16BigEndian(reply, 2);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), 70);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), ssrc);
        Encoding.ASCII.GetBytes(ip).CopyTo(reply, 8);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(72), (ushort)port);
        return reply;
    }
}

internal sealed class FakeUdpFactory(params FakeUdp[] sockets) : IUdpFactory
{
    private readonly Queue<FakeUdp> _sockets = new(sockets);

    public List<FakeUdp> Created { get; } = [];

    public IUdpConnection Create()
    {
        var udp = _sockets.Count > 0 ? _sockets.Dequeue() : new FakeUdp();
        Created.Add(udp);
        return udp;
    }
}

/// <summary>DAVE fake: records every call; results are configurable.</summary>
internal sealed class FakeDave : IDaveSession
{
    public List<string> Calls { get; } = [];

    public byte[] KeyPackage { get; set; } = [0xAA, 0xBB];

    public byte[]? CommitWelcome { get; set; } = [0xC0, 0x11];

    public bool? CommitOk { get; set; } = true;

    public bool WelcomeOk { get; set; } = true;

    public bool Throw { get; set; }

    public byte[] LastData { get; private set; } = [];

    public IReadOnlyCollection<ulong> LastRecognized { get; private set; } = [];

    public DaveProposalsOperation? LastOperation { get; private set; }

    public int ProtocolVersion { get; private set; }

    public bool Ready { get; set; }

    public bool Disposed { get; private set; }

    public void Reinit(int protocolVersion, ulong userId, ulong channelId)
    {
        ProtocolVersion = protocolVersion;
        Record($"Reinit({protocolVersion},{userId},{channelId})");
    }

    public void Reset() => Record("Reset");

    public byte[] GetSerializedKeyPackage()
    {
        Record("GetSerializedKeyPackage");
        return KeyPackage;
    }

    public void SetExternalSender(ReadOnlySpan<byte> data)
    {
        LastData = data.ToArray();
        Record("SetExternalSender");
    }

    public byte[]? ProcessProposals(DaveProposalsOperation operation, ReadOnlySpan<byte> proposals, IReadOnlyCollection<ulong> recognizedUserIds)
    {
        LastOperation = operation;
        LastData = proposals.ToArray();
        LastRecognized = recognizedUserIds;
        Record("ProcessProposals");
        return Throw ? throw new InvalidOperationException("boom") : CommitWelcome;
    }

    public bool? ProcessCommit(ReadOnlySpan<byte> commit)
    {
        LastData = commit.ToArray();
        Record("ProcessCommit");
        return Throw ? throw new InvalidOperationException("boom") : CommitOk;
    }

    public bool ProcessWelcome(ReadOnlySpan<byte> welcome, IReadOnlyCollection<ulong> recognizedUserIds)
    {
        LastData = welcome.ToArray();
        LastRecognized = recognizedUserIds;
        Record("ProcessWelcome");
        return WelcomeOk;
    }

    public void SetPassthroughMode(bool passthrough) => Record($"SetPassthroughMode({passthrough})");

    public byte[] EncryptOpus(uint ssrc, ReadOnlySpan<byte> frame) => frame.ToArray();

    public void RegisterVideoSsrc(uint ssrc) => Record($"RegisterVideoSsrc({ssrc})");

    public byte[] EncryptH264(uint ssrc, ReadOnlySpan<byte> accessUnit) => accessUnit.ToArray();

    public void Dispose() => Disposed = true;

    private void Record(string call)
    {
        lock (Calls)
        {
            Calls.Add(call);
        }
    }

    public async Task WaitForCallAsync(string call)
    {
        var deadline = DateTime.UtcNow + Wait.Timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (Calls)
            {
                if (Calls.Contains(call))
                {
                    return;
                }
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"DAVE call {call} not seen; calls: {string.Join(", ", Calls)}");
    }
}

/// <summary>Real timers, fixed wall clock (heartbeat "t" is deterministic).</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>A scripted voice server: HELLO, READY and SESSION_DESCRIPTION answering IDENTIFY/SELECT_PROTOCOL.</summary>
internal static class VoiceServerScript
{
    public const uint Ssrc = 1234;

    public static FakeWebSocket Create(
        string modes = "[\"xsalsa20_poly1305_lite\",\"aead_xchacha20_poly1305_rtpsize\",\"xsalsa20_poly1305\"]",
        int daveVersion = 0,
        int heartbeatMs = 60_000,
        bool describe = true)
    {
        var ws = new FakeWebSocket();
        ws.OnText = (socket, op, d) =>
        {
            if (op == VoiceOp.Identify)
            {
                socket.Push(VoiceOp.Hello, $"{{\"heartbeat_interval\":{heartbeatMs}}}", seq: 1);
                socket.Push(VoiceOp.Ready, $"{{\"ssrc\":{Ssrc},\"ip\":\"198.51.100.1\",\"port\":50001,\"modes\":{modes},\"streams\":[]}}", seq: 2);
            }
            else if (op == VoiceOp.SelectProtocol && describe)
            {
                var mode = d.GetProperty("data").GetProperty("mode").GetString();
                var key = string.Join(",", Enumerable.Range(0, 32));
                socket.Push(VoiceOp.SessionDescription,
                    $"{{\"mode\":\"{mode}\",\"secret_key\":[{key}],\"dave_protocol_version\":{daveVersion}}}", seq: 3);
            }
        };
        return ws;
    }
}

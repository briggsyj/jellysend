using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using JellySend.Discord.Transport;

namespace JellySend.Tests.Discord;

/// <summary>Deterministic clock: timers fire only on <see cref="Advance"/>.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _active = [];
    private readonly Channel<ManualTimer> _created = Channel.CreateUnbounded<ManualTimer>();
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _ticks;
        }
    }

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime);
        timer.Change(dueTime, period);
        _created.Writer.TryWrite(timer);
        return timer;
    }

    /// <summary>The next timer created (and not yet disposed), in creation order.</summary>
    public async Task<ManualTimer> NextTimerAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var timer = await _created.Reader.ReadAsync(cts.Token);
            if (!timer.Disposed)
            {
                return timer;
            }
        }
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _ticks += by.Ticks;
            due = _active.Where(t => t.DueAt <= _ticks).ToList();
            foreach (var t in due)
            {
                _active.Remove(t);
            }
        }

        foreach (var t in due)
        {
            t.Fire();
        }
    }

    internal sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime { get; } = dueTime;

        public long DueAt { get; private set; }

        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                owner._active.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan && !Disposed)
                {
                    DueAt = owner._ticks + dueTime.Ticks;
                    owner._active.Add(this);
                }
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._lock)
            {
                Disposed = true;
                owner._active.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Scripted gateway peer: the test pushes server frames and reads what the client sent.</summary>
internal sealed class FakeSocket : IWebSocketConnection
{
    private readonly Channel<WebSocketFrame> _inbound = Channel.CreateUnbounded<WebSocketFrame>();
    private readonly Channel<JsonElement> _sent = Channel.CreateUnbounded<JsonElement>();

    public Uri? Uri { get; private set; }

    public int? ClosedWith { get; private set; }

    public bool Disposed { get; private set; }

    public Exception? ConnectError { get; init; }

    public bool FailSends { get; set; }

    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        Uri = uri;
        return ConnectError is null ? Task.CompletedTask : Task.FromException(ConnectError);
    }

    public Task SendTextAsync(string json, CancellationToken ct)
    {
        if (FailSends)
        {
            return Task.FromException(new IOException("send failed"));
        }

        _sent.Writer.TryWrite(JsonDocument.Parse(json).RootElement.Clone());
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => throw new NotSupportedException();

    public Task<WebSocketFrame> ReceiveAsync(CancellationToken ct) => _inbound.Reader.ReadAsync(ct).AsTask();

    public Task CloseAsync(int code, CancellationToken ct)
    {
        ClosedWith ??= code;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    // ---- server side ----

    public void Push(string json) => _inbound.Writer.TryWrite(new WebSocketFrame(true, Encoding.UTF8.GetBytes(json)));

    public void PushZlib(string json)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(Encoding.UTF8.GetBytes(json));
        }

        _inbound.Writer.TryWrite(new WebSocketFrame(false, buffer.ToArray()));
    }

    public void Op(int op, string data = "null") => Push($$"""{"op":{{op}},"d":{{data}}}""");

    public void Hello(int intervalMs = 41250) => Op(10, $$"""{"heartbeat_interval":{{intervalMs}}}""");

    public void Dispatch(string name, string data, long? seq)
    {
        var s = seq is { } v ? $",\"s\":{v}" : string.Empty;
        Push($$"""{"op":0,"t":"{{name}}","d":{{data}}{{s}}}""");
    }

    public void PeerClose(int? code) => _inbound.Writer.TryWrite(new WebSocketFrame(false, default, IsClose: true, CloseCode: code));

    public void Break() => _inbound.Writer.TryComplete(new IOException("connection reset"));

    public async Task<JsonElement> NextSentAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _sent.Reader.ReadAsync(cts.Token);
    }

    public async Task<JsonElement> ExpectAsync(int op)
    {
        var sent = await NextSentAsync();
        Assert.Equal(op, sent.GetProperty("op").GetInt32());
        return sent;
    }

    public bool HasSent => _sent.Reader.TryPeek(out _);
}

internal sealed class FakeSocketFactory : IWebSocketFactory
{
    private readonly Channel<FakeSocket> _created = Channel.CreateUnbounded<FakeSocket>();

    public Queue<Exception> ConnectErrors { get; } = new();

    public int Count { get; private set; }

    public IWebSocketConnection Create()
    {
        var socket = new FakeSocket { ConnectError = ConnectErrors.Count > 0 ? ConnectErrors.Dequeue() : null };
        Count++;
        _created.Writer.TryWrite(socket);
        return socket;
    }

    public async Task<FakeSocket> NextAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _created.Reader.ReadAsync(cts.Token);
    }
}

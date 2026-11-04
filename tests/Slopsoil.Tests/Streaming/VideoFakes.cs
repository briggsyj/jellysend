using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Slopsoil.Streaming.Process;
using Slopsoil.Streaming.Video;

namespace Slopsoil.Tests.Streaming;

/// <summary>Canned H.264 NAL units (same bytes as <c>tests/test_video_player.py</c>).</summary>
internal static class Nals
{
    public static readonly byte[] Aud = [0x09, 0xF0];
    public static readonly byte[] Pps = [0x68, 0xCE, 0x38, 0x80];
    public static readonly byte[] Sei = [0x06, 0x05, 0x01, 0x80];
    public static readonly byte[] Idr = [0x65, 0x88, 0x84, 0x00, 0x10];
    public static readonly byte[] P1 = [0x41, 0x9A, 0x02, 0x03];
    public static readonly byte[] P2 = [0x41, 0x9A, 0x04, 0x05];
    public static readonly byte[] Filler = [0x0C, 0xFF, 0xFF];
    public static readonly byte[] StartCode = [0, 0, 0, 1];

    /// <summary>Baseline SPS without VUI (Python <c>_make_sps()</c>).</summary>
    public static readonly byte[] Sps = [0x67, 0x42, 0xC0, 0x1E, 0xDA, 0x10, 0x99];

    public static byte[] AnnexB(params byte[][] nals) => [.. nals.SelectMany(n => StartCode.Concat(n))];

    /// <summary>
    /// <paramref name="count"/> frames of AUD + slice (the first an IDR), ending in AUD + start code so every
    /// frame completes while reading, before end of stream.
    /// </summary>
    public static byte[] Frames(int count) =>
        [.. AnnexB([.. Enumerable.Range(0, count).SelectMany(i => new[] { Aud, i == 0 ? Idr : P1 }), Aud]), .. StartCode];
}

/// <summary>
/// Fake clock: timers created on the video player thread (pacing sleeps, drain waits) fire at once and move
/// the clock forward by their due time, so pacing never really sleeps; any other timer (overlay refresh, stats
/// log) waits for <see cref="Advance"/>. <see cref="AutoFire"/> narrows which player-thread timers auto-fire.
/// </summary>
internal sealed class SteppingTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<Timer> _pending = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Durations of the auto-fired timers, in order.</summary>
    public ConcurrentQueue<TimeSpan> Sleeps { get; } = new();

    public Func<TimeSpan, bool> AutoFire { get; set; } = _ => true;

    /// <summary>Player-thread timers that were held back by <see cref="AutoFire"/> and are waiting.</summary>
    public int HeldPlayerTimers
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count(t => t.Auto);
            }
        }
    }

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
        var timer = new Timer(this, callback, state, Thread.CurrentThread.Name == "H264VideoPlayer");
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<Timer> due;
        lock (_lock)
        {
            _ticks += by.Ticks;
            due = [.. _pending.Where(t => t.DueAt <= _ticks)];
            foreach (var t in due)
            {
                _pending.Remove(t);
            }
        }

        foreach (var t in due)
        {
            t.Fire();
        }
    }

    internal sealed class Timer(SteppingTimeProvider owner, TimerCallback callback, object? state, bool auto) : ITimer
    {
        private bool _disposed;

        public bool Auto => auto;

        public long DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                lock (owner._lock)
                {
                    owner._pending.Remove(this);
                }

                return true;
            }

            if (auto && owner.AutoFire(dueTime))
            {
                lock (owner._lock)
                {
                    owner._ticks += dueTime.Ticks;
                }

                owner.Sleeps.Enqueue(dueTime);
                ThreadPool.QueueUserWorkItem(_ => Fire());
                return true;
            }

            lock (owner._lock)
            {
                owner._pending.Remove(this);
                if (!_disposed)
                {
                    DueAt = owner._ticks + dueTime.Ticks;
                    owner._pending.Add(this);
                }
            }

            return true;
        }

        public void Fire()
        {
            if (!_disposed)
            {
                callback(state);
            }
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                _disposed = true;
                owner._pending.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>stdout stand-in returning canned bytes in fixed-size chunks; optionally blocks at the end until closed.</summary>
internal sealed class ChunkStream(byte[] data, int step, bool blockAtEnd) : Stream
{
    private readonly ManualResetEventSlim _released = new();
    private int _position;

    public Action? OnRead { get; set; }

    public bool Closed { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public void Release() => _released.Set();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        OnRead?.Invoke();
        ObjectDisposedException.ThrowIf(Closed, this);
        if (_position >= data.Length)
        {
            if (blockAtEnd)
            {
                _released.Wait(TimeSpan.FromSeconds(30));
                ObjectDisposedException.ThrowIf(Closed, this);
            }

            return 0;
        }

        var n = Math.Min(Math.Min(step, buffer.Length), data.Length - _position);
        data.AsSpan(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Closed = true;
        _released.Set();
        base.Dispose(disposing);
    }
}

/// <summary>Scripted FFmpeg process (Python's <c>_FakeProc</c>).</summary>
internal sealed class FakeMediaProcess : IMediaProcess
{
    private readonly ChunkStream _stdout;
    private volatile bool _running;

    public FakeMediaProcess(byte[]? stdout = null, string stderr = "", bool running = false, bool waitTimesOut = false, int step = 65_536, bool blockAtEnd = false)
    {
        _stdout = new ChunkStream(stdout ?? [], step, blockAtEnd);
        StandardError = new StringReader(stderr);
        _running = running || blockAtEnd;
        WaitTimesOut = waitTimesOut;
    }

    /// <summary>Order of close/term/kill calls.</summary>
    public ConcurrentQueue<string> Events { get; } = new();

    public ChunkStream Stdout => _stdout;

    public Stream StandardOutput => _stdout;

    public TextReader StandardError { get; }

    public bool HasExited => !_running;

    public int ExitCode => 0;

    public bool WaitTimesOut { get; set; }

    public int Terminated => Events.Count(e => e == "term");

    public int Killed => Events.Count(e => e == "kill");

    public bool StdoutClosed => Events.Contains("close");

    public bool Disposed { get; private set; }

    public void CloseStandardOutput()
    {
        Events.Enqueue("close");
        _stdout.Dispose();
    }

    public void Terminate()
    {
        Events.Enqueue("term");
        _stdout.Release();
    }

    public void Kill()
    {
        Events.Enqueue("kill");
        _running = false;
        _stdout.Release();
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        if (WaitTimesOut && timeout != Timeout.InfiniteTimeSpan)
        {
            return false;
        }

        _running = false;
        return true;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Hands out scripted processes in order and records every command line.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly ConcurrentQueue<FakeMediaProcess> _processes = new();

    public FakeProcessRunner(params FakeMediaProcess[] processes)
    {
        foreach (var p in processes)
        {
            _processes.Enqueue(p);
        }
    }

    public ConcurrentQueue<IReadOnlyList<string>> Started { get; } = new();

    public List<IReadOnlyList<string>> Runs { get; } = [];

    /// <summary>Runs on the caller's thread right after a process "starts".</summary>
    public Action<IReadOnlyList<string>>? OnStart { get; set; }

    public Exception? StartError { get; set; }

    /// <summary>Probe results; null result = executable missing.</summary>
    public Func<IReadOnlyList<string>, ProcessResult?> OnRun { get; set; } = _ => new ProcessResult(0, "", "");

    public void Add(FakeMediaProcess process) => _processes.Enqueue(process);

    public IMediaProcess Start(IReadOnlyList<string> args)
    {
        if (StartError is { } error)
        {
            throw error;
        }

        Started.Enqueue(args);
        var process = _processes.TryDequeue(out var p) ? p : new FakeMediaProcess();
        OnStart?.Invoke(args);
        return process;
    }

    public ProcessResult Run(IReadOnlyList<string> args, TimeSpan timeout)
    {
        Runs.Add(args);
        return OnRun(args) ?? throw new Win32Exception(2, "No such file or directory");
    }
}

internal sealed class FakeFifoFactory : IFifoFactory
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public void Create(string path) => Calls.Enqueue("create " + path);

    public void Remove(string path) => Calls.Enqueue("remove " + path);
}

/// <summary>Records the access units the player sends; can run a hook and the pacer per frame.</summary>
internal sealed class RecordingFrameSink : IVideoFrameSink
{
    public List<IReadOnlyList<byte[]>> Frames { get; } = [];

    public List<int> TimestampIncrements { get; } = [];

    /// <summary>Packets each frame pretends to have (the pacer is called after each).</summary>
    public int PacketsPerFrame { get; set; } = 1;

    public int PacketsSent { get; private set; }

    /// <summary>Called on the player thread after each frame with the frame count so far.</summary>
    public Action<int>? OnSend { get; set; }

    public int Count
    {
        get
        {
            lock (Frames)
            {
                return Frames.Count;
            }
        }
    }

    public void SendVideoFrame(IReadOnlyList<byte[]> nals, int timestampIncrement, PacketPacer? pacer)
    {
        lock (Frames)
        {
            Frames.Add(nals);
            TimestampIncrements.Add(timestampIncrement);
        }

        for (var i = 0; i < PacketsPerFrame; i++)
        {
            PacketsSent++;
            if (pacer is not null && !pacer(i, PacketsPerFrame))
            {
                break;
            }
        }

        OnSend?.Invoke(Count);
    }
}

/// <summary>Captures log lines (level + rendered message).</summary>
internal sealed class VideoTestLogger : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception)));

    public string Text => string.Join('\n', Entries.Select(e => e.Message));
}

internal static class Waits
{
    /// <summary>Poll until <paramref name="condition"/> holds (fails after 10 s).</summary>
    public static async Task UntilAsync(Func<bool> condition, string what = "condition")
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    public static async Task<T> Within<T>(Task<T> task)
    {
        return await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}

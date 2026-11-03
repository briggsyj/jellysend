using Microsoft.Extensions.Logging;
using Slopsoil.Streaming.Audio;
using Slopsoil.Streaming.Contracts;

namespace Slopsoil.Tests.Streaming;

/// <summary>Stand-in for the video player's clock (Python's <c>_Clock</c>). Fps 0 = "no frame rate known".</summary>
internal sealed class FakeMediaClock : IMediaClock
{
    public double Fps { get; set; }

    public Func<double> PositionFn { get; set; } = () => 0.0;

    public double Position => PositionFn();

    public int RestartSeq { get; set; }

    public double SegmentOffset { get; set; }

    /// <summary>Defaults to 1: video is flowing (Python's <c>started=True</c>).</summary>
    public int SegmentFrames { get; set; } = 1;

    public bool SeekPending { get; set; }

    public Func<bool> SourceActive { get; set; } = () => false;

    public bool IsSourceActive => SourceActive();

    public PauseGate Pause { get; } = new();

    public List<FakeFollowerHandle> Handles { get; } = [];

    public IAudioFollowerHandle RegisterAudioFollower()
    {
        var h = new FakeFollowerHandle();
        Handles.Add(h);
        return h;
    }
}

internal sealed class FakeFollowerHandle : IAudioFollowerHandle
{
    public int Signals { get; private set; }

    public bool Disposed { get; private set; }

    public bool SignaledBeforeDispose { get; private set; }

    public void SignalDrained() => Signals++;

    public void Dispose()
    {
        SignaledBeforeDispose = Signals > 0;
        Disposed = true;
    }
}

internal sealed class FakeAudioStats : IAudioStats
{
    public int Silence { get; private set; }

    public int Skips { get; private set; }

    public List<double> Drifts { get; } = [];

    public Action? OnSilence { get; set; }

    public void AudioSilenceStep()
    {
        Silence++;
        OnSilence?.Invoke();
    }

    public void AudioSkipStep() => Skips++;

    public void AudioDrift(double milliseconds) => Drifts.Add(milliseconds);
}

/// <summary>Scripted PCM: each ReadFrame returns the next scripted chunk (or nothing once exhausted).</summary>
internal sealed class FakePcm(IEnumerable<byte[]> reads) : IPcmSource
{
    public const int Frame = 3840;

    private readonly Queue<byte[]> _reads = new(reads);

    public int Reads { get; private set; }

    public int Drains { get; private set; }

    public int DrainResult { get; set; }

    /// <summary><paramref name="frames"/> whole frames filled with their index, then <paramref name="extra"/> stray bytes.</summary>
    public static FakePcm Frames(int frames, int extra = 0)
    {
        var list = Enumerable.Range(0, frames).Select(i => Enumerable.Repeat((byte)i, Frame).ToArray()).ToList();
        if (extra > 0)
        {
            list.Add(new byte[extra]);
        }

        return new FakePcm(list);
    }

    public int ReadFrame(Span<byte> frame)
    {
        Reads++;
        if (!_reads.TryDequeue(out var chunk))
        {
            return 0;
        }

        chunk.CopyTo(frame);
        return chunk.Length;
    }

    public int DrainAvailable()
    {
        Drains++;
        return DrainResult;
    }
}

/// <summary>Python's <c>_FakeEncoder</c>: "OPUS" + the frame's first byte.</summary>
internal sealed class FakeEncoder : IAudioFrameEncoder
{
    public int Calls { get; private set; }

    public bool Disposed { get; private set; }

    public byte[] Encode(ReadOnlySpan<byte> pcm)
    {
        Assert.Equal(FakePcm.Frame, pcm.Length);
        Calls++;
        return [(byte)'O', (byte)'P', (byte)'U', (byte)'S', pcm[0]];
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// A clock whose timers fire immediately (Python's <c>_FastEvent</c>): every wait is recorded, the hook runs,
/// time jumps by the wait (unless frozen), and the callback fires synchronously. Fully deterministic.
/// </summary>
internal sealed class StepTimeProvider : TimeProvider
{
    private long _ticks = TimeSpan.FromSeconds(100).Ticks;

    public List<TimeSpan> Waits { get; } = [];

    /// <summary>When false the clock never moves (Python test monkeypatching <c>perf_counter</c>).</summary>
    public bool AdvanceOnWait { get; set; } = true;

    public Action<TimeSpan>? OnWait { get; set; }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public int WaitsOf(int milliseconds) => Waits.Count(w => w == TimeSpan.FromMilliseconds(milliseconds));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Waits.Add(dueTime);
        OnWait?.Invoke(dueTime);
        if (AdvanceOnWait)
        {
            Interlocked.Add(ref _ticks, dueTime.Ticks);
        }

        callback(state);
        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}

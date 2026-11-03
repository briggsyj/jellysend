namespace Slopsoil.Streaming.Contracts;

/// <summary>
/// The video player's clock as the audio sender sees it (the <c>sync_source</c> in
/// <c>golive.GoLiveAudioSender</c>). Implemented by <c>Video.VideoPlayer</c>; faked in audio tests.
/// All members are safe to read from any thread.
/// </summary>
public interface IMediaClock
{
    /// <summary>Output frame rate.</summary>
    double Fps { get; }

    /// <summary>Master clock in media seconds: <c>SegmentOffset + SegmentFrames / Fps</c> (de-biasing is the follower's job).</summary>
    double Position { get; }

    /// <summary>Bumped on every FFmpeg (re)start that begins a new segment: seek, aspect/debug change, encoder fallback.</summary>
    int RestartSeq { get; }

    /// <summary>Media position the current segment started at.</summary>
    double SegmentOffset { get; }

    /// <summary>Frames sent in the current segment (0 until its first frame).</summary>
    int SegmentFrames { get; }

    /// <summary>A seek was requested and the old FFmpeg is not gone yet: its audio is stale.</summary>
    bool SeekPending { get; }

    /// <summary>True while the player may still produce output, including the gap while FFmpeg restarts.</summary>
    bool IsSourceActive { get; }

    /// <summary>Shared pause gate: closed while playback is paused.</summary>
    PauseGate Pause { get; }

    /// <summary>
    /// Register the audio follower. After each restart the player waits (≤ 0.5 s) for
    /// <see cref="IAudioFollowerHandle.SignalDrained"/> before starting the new FFmpeg, so the follower can
    /// discard the old process's FIFO audio first. Dispose the handle when the sender exits.
    /// </summary>
    IAudioFollowerHandle RegisterAudioFollower();
}

/// <summary>The audio sender's registration with the player (<c>register_audio_follower</c>).</summary>
public interface IAudioFollowerHandle : IDisposable
{
    /// <summary>The old segment's audio has been drained; the player may start the next FFmpeg.</summary>
    void SignalDrained();
}

/// <summary>Counters the audio follower reports into the stream diagnostics (implemented by <c>Diagnostics.StreamDiagnostics</c>).</summary>
public interface IAudioStats
{
    /// <summary>A 20 ms frame of silence sent without consuming PCM.</summary>
    void AudioSilenceStep();

    /// <summary>A 20 ms PCM frame skipped to catch up.</summary>
    void AudioSkipStep();

    /// <summary>Latest measured audio-minus-video drift (milliseconds, target removed).</summary>
    void AudioDrift(double milliseconds);
}

/// <summary>A gate that is open while playing and closed while paused (Python's <c>pause_gate</c> Event).</summary>
public sealed class PauseGate
{
    private readonly object _lock = new();
    private TaskCompletionSource _open = NewOpen();

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return _open.Task.IsCompleted;
            }
        }
    }

    public void Open()
    {
        lock (_lock)
        {
            _open.TrySetResult();
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            if (_open.Task.IsCompleted)
            {
                _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    /// <summary>True if the gate opened within <paramref name="timeout"/>.</summary>
    public bool WaitOpen(TimeSpan timeout, CancellationToken ct = default)
    {
        Task task;
        lock (_lock)
        {
            task = _open.Task;
        }

        try
        {
            return task.Wait(timeout, ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static TaskCompletionSource NewOpen()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}

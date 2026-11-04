namespace Slopsoil.Streaming.Contracts;

/// <summary>
/// The playout clock of one segment (spec 004 FR-004): video frame <c>n</c> is due at
/// <c>Origin + n / Fps</c> and audio frame <c>k</c> at <c>Origin + k · 20 ms − STREAM_AV_SYNC_MS</c>.
/// </summary>
/// <param name="Segment">The <see cref="IMediaClock.RestartSeq"/> of the segment this origin belongs to.</param>
/// <param name="Origin">
/// <see cref="TimeProvider.GetTimestamp"/> of the player's time provider at which the segment's frame 0 is due;
/// moved forward by every pause so both streams resume where they stopped.
/// </param>
public sealed record PlayoutAnchor(int Segment, long Origin);

/// <summary>
/// The video player's clock as the audio sender sees it (the <c>sync_source</c> in
/// <c>golive.GoLiveAudioSender</c>). Implemented by <c>Video.VideoPlayer</c>; faked in audio tests.
/// All members are safe to read from any thread.
/// </summary>
public interface IMediaClock
{
    /// <summary>
    /// The current segment's playout origin, published when its first frame is due (after the pre-roll) and
    /// pause-adjusted; null before that and after the player ended. Audio and video share the player's time provider.
    /// </summary>
    PlayoutAnchor? Anchor { get; }

    /// <summary>Output frame rate.</summary>
    double Fps { get; }

    /// <summary>Master clock in media seconds: <c>SegmentOffset + SegmentFrames / Fps</c>.</summary>
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
    /// Register the audio sender. After each restart the player waits (≤ 0.5 s) for
    /// <see cref="IAudioFollowerHandle.SignalDrained"/> before starting the new FFmpeg, so the sender can
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

/// <summary>Counters the audio sender reports into the stream diagnostics (implemented by <c>Diagnostics.StreamDiagnostics</c>).</summary>
public interface IAudioStats
{
    /// <summary>A 20 ms frame of silence sent because no PCM was buffered for its slot (underrun).</summary>
    void AudioSilenceStep();

    /// <summary>A 20 ms PCM frame dropped because its slot had passed (late after an underrun).</summary>
    void AudioSkipStep();

    /// <summary>Latest audio-minus-video send position on the shared clock (milliseconds, av-sync offset removed).</summary>
    void AudioDrift(double milliseconds);

    /// <summary>PCM buffered ahead of playout.</summary>
    void AudioBuffered(TimeSpan buffered);
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

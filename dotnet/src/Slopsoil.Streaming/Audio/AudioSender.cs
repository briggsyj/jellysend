using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Slopsoil.Discord.Native;
using Slopsoil.Streaming.Contracts;

namespace Slopsoil.Streaming.Audio;

/// <summary>Sends one encoded 20 ms Opus frame (normally <see cref="RtpMediaSender.SendOpus"/>).</summary>
public delegate void OpusSink(ReadOnlySpan<byte> opus);

/// <summary>Correction steps the follower has made: silence inserted, PCM frames dropped.</summary>
public readonly record struct AudioCorrections(long Silence, long Dropped);

/// <summary>
/// Go-live audio sender, a port of <c>golive.GoLiveAudioSender</c>: on a dedicated thread, reads 20 ms PCM
/// frames from FFmpeg's audio FIFO, encodes them to Opus and sends them, paced to an absolute wall-clock
/// schedule. When a media clock (the video player) is given, audio follows it per FR-054: settling
/// alignment after each segment's first frame, correction of large persistent drift only, stale-audio
/// drain on every FFmpeg restart, silence while a seek is pending or the new segment has no picture yet,
/// and the pause gate.
/// </summary>
public sealed class AudioSender
{
    private const int FrameBytes = OpusEncoder.FrameBytes;
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan PausePoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SourceGapPoll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MinPacingWait = TimeSpan.FromMilliseconds(1);

    private readonly IPcmSource _pcm;
    private readonly IMediaClock? _clock;
    private readonly OpusSink _send;
    private readonly int _avSyncMs;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Func<IAudioFrameEncoder> _encoderFactory;
    private readonly IAudioFollowerHandle? _handle;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _frame = new byte[FrameBytes];
    private readonly byte[] _silence = new byte[FrameBytes];
    private int _state; // 0 = new, 1 = started, 2 = finished
    private long _framesSent;

    /// <summary>Send through a go-live connection's <see cref="RtpMediaSender"/> (RTP, DAVE, transport crypto).</summary>
    public AudioSender(
        IPcmSource pcm,
        IMediaClock? clock,
        IAudioStats? stats,
        RtpMediaSender sender,
        int avSyncMs,
        ILogger? logger = null,
        TimeProvider? time = null,
        Func<IAudioFrameEncoder>? encoderFactory = null)
        : this(pcm, clock, stats, (sender ?? throw new ArgumentNullException(nameof(sender))).SendOpus, avSyncMs, logger, time, encoderFactory)
    {
    }

    /// <param name="pcm">FFmpeg's PCM output (the audio FIFO).</param>
    /// <param name="clock">The video player's clock to follow; null free-runs on the wall clock.</param>
    /// <param name="stats">Diagnostics sink for drift and correction steps.</param>
    /// <param name="send">Where encoded frames go.</param>
    /// <param name="avSyncMs"><c>STREAM_AV_SYNC_MS</c>: positive advances audio, negative delays it (±5000).</param>
    /// <param name="logger">Defaults to a null logger.</param>
    /// <param name="time">Pacing and follower clock; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="encoderFactory">Created on the sender thread; defaults to libopus.</param>
    public AudioSender(
        IPcmSource pcm,
        IMediaClock? clock,
        IAudioStats? stats,
        OpusSink send,
        int avSyncMs,
        ILogger? logger = null,
        TimeProvider? time = null,
        Func<IAudioFrameEncoder>? encoderFactory = null)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentNullException.ThrowIfNull(send);
        _pcm = pcm;
        _clock = clock;
        _send = send;
        _avSyncMs = Math.Clamp(avSyncMs, -5000, 5000);
        _log = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _encoderFactory = encoderFactory ?? (static () => new OpusFrameEncoder());
        Follower = new AudioFollower(clock?.Fps ?? 0, clock?.RestartSeq ?? 0, clock?.SegmentOffset ?? 0, stats, _log);
        FollowerNow = () => _time.GetTimestamp() / (double)_time.TimestampFrequency;
        _handle = clock?.RegisterAudioFollower();
    }

    /// <summary>Completes (never faults) once the sender has stopped: Stop, end of input, or an error (logged).</summary>
    public Task Completion => _completion.Task;

    /// <summary>Correction steps so far (thread-safe).</summary>
    public AudioCorrections Corrections => new(Follower.SilenceSteps, Follower.DroppedSteps);

    /// <summary>Opus frames handed to the sink.</summary>
    public long FramesSent => Interlocked.Read(ref _framesSent);

    internal AudioFollower Follower { get; }

    /// <summary>Monotonic seconds the follower measures persistence with (a seam for tests).</summary>
    internal Func<double> FollowerNow { get; set; }

    /// <summary>Audio's media position (seconds) in the current segment.</summary>
    internal double AudioPosition => Follower.AudioPosition;

    /// <summary>Start the sender thread (once).</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return;
        }

        new Thread(Run) { IsBackground = true, Name = "GoLiveAudio" }.Start();
    }

    /// <summary>Ask the sender to stop; idempotent. A sender that was never started completes at once.</summary>
    public void Stop()
    {
        _stop.Cancel();
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
        {
            Finish();
        }
    }

    private bool Stopping => _stop.IsCancellationRequested;

    private void Run()
    {
        try
        {
            SendLoop();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GoLiveAudioSender error");
        }
        finally
        {
            Finish();
        }
    }

    private void Finish()
    {
        // Don't leave the player waiting on a follower that's gone.
        if (_handle is not null)
        {
            try
            {
                _handle.SignalDrained();
                _handle.Dispose();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GoLiveAudio: unregistering from the video player failed");
            }
        }

        _log.LogInformation("GoLiveAudioSender stopped");
        _completion.TrySetResult();
    }

    /// <summary>The sender thread's body (<c>_send_audio</c>); runs synchronously on the caller in tests.</summary>
    internal void SendLoop()
    {
        Interlocked.CompareExchange(ref _state, 1, 0); // running: Stop no longer finishes on its own
        using var encoder = _encoderFactory();
        var target = _avSyncMs / 1000.0; // desired audio-minus-video position

        // Positive offset discards backlogged initial audio (fixes "audio behind"); negative holds the start.
        if (_avSyncMs > 0)
        {
            for (var i = 0; i < _avSyncMs / 20; i++)
            {
                if (Stopping || _pcm.ReadFrame(_frame) < FrameBytes)
                {
                    break;
                }

                Follower.FrameConsumed();
            }
        }
        else if (_avSyncMs < 0)
        {
            WaitStop(TimeSpan.FromMilliseconds(-_avSyncMs));
        }

        _log.LogInformation("GoLiveAudio: starting audio transmission (av_sync_ms={AvSyncMs})", _avSyncMs);
        var t0 = _time.GetTimestamp();
        long loops = 0;

        while (!Stopping)
        {
            var clock = _clock;
            if (clock is not null && !clock.Pause.IsOpen)
            {
                while (!WaitPauseOpen(clock.Pause))
                {
                    if (Stopping)
                    {
                        return;
                    }

                    // A seek/aspect change while paused restarts FFmpeg now; drain the old audio
                    // before the new process fills the FIFO.
                    CheckRestart();
                }

                Follower.ResetPersistence();
                t0 = _time.GetTimestamp(); // restart pacing after the pause
                loops = 0;
                continue;
            }

            if (CheckRestart())
            {
                // FFmpeg restarted: old audio dropped, position re-anchored.
                t0 = _time.GetTimestamp();
                loops = 0;
                continue;
            }

            byte[]? pcm = null;
            if (clock is not null && clock.SeekPending)
            {
                // Seek requested but the old FFmpeg isn't gone yet: its audio is stale, so play silence,
                // but keep draining it without blocking or FFmpeg stalls on the full FIFO.
                _pcm.DrainAvailable();
                pcm = _silence;
            }
            else if (clock is not null && clock.SegmentFrames > 0)
            {
                switch (Follower.Follow(clock, target, FollowerNow()))
                {
                    case FollowAction.Silence:
                        pcm = _silence;
                        break;
                    case FollowAction.Skip:
                        if (_pcm.ReadFrame(_frame) == FrameBytes)
                        {
                            Follower.CommitSkip();
                        }

                        break;
                }
            }
            else if (clock is not null && Follower.TryWaitSilence())
            {
                // New segment's video hasn't produced a frame yet: wait in silence (bounded) so its
                // audio starts with the picture instead of ahead of it.
                pcm = _silence;
            }

            if (pcm is null)
            {
                if (_pcm.ReadFrame(_frame) < FrameBytes)
                {
                    // Short read = the current FFmpeg closed the FIFO's write end. If the player is
                    // restarting FFmpeg, wait for the new writer and resume.
                    if (clock is not null && clock.IsSourceActive)
                    {
                        if (WaitStop(SourceGapPoll))
                        {
                            break;
                        }

                        t0 = _time.GetTimestamp(); // reset pacing after the gap
                        loops = 0;
                        continue;
                    }

                    break;
                }

                Follower.FrameConsumed();
                pcm = _frame;
            }

            _send(encoder.Encode(pcm));
            Interlocked.Increment(ref _framesSent);
            loops++;

            // Wall-clock pacing to an absolute schedule, same as the video player.
            var delay = (FrameDuration * loops) - _time.GetElapsedTime(t0);
            if (delay > MinPacingWait && WaitStop(delay))
            {
                break;
            }
        }
    }

    /// <summary>Handle an FFmpeg restart (drain + re-anchor + tell the player). True if one happened.</summary>
    internal bool CheckRestart()
    {
        var clock = _clock;
        if (clock is null)
        {
            return false;
        }

        var seq = clock.RestartSeq;
        if (seq == Follower.SeenSeq)
        {
            return false;
        }

        var dropped = _pcm.DrainAvailable();
        Follower.Rebase(seq, clock.SegmentOffset);
        _handle?.SignalDrained();
        _log.LogInformation(
            "GoLiveAudio: source restarted at {Offset:F1}s — dropped {Dropped} stale bytes", Follower.SegmentBase, dropped);
        return true;
    }

    /// <summary>Wait on the stop signal (Python's <c>_end.wait</c>): true if a stop has been requested.</summary>
    private bool WaitStop(TimeSpan timeout)
    {
        if (Stopping)
        {
            return true;
        }

        var elapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (_time.CreateTimer(static s => ((TaskCompletionSource)s!).TrySetResult(), elapsed, timeout, Timeout.InfiniteTimeSpan))
        {
            try
            {
                elapsed.Task.Wait(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                // Stop requested.
            }
        }

        return Stopping;
    }

    /// <summary>Wait up to <see cref="PausePoll"/> for the gate to open (or a stop): true if it opened.</summary>
    private bool WaitPauseOpen(PauseGate gate)
    {
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        using (_time.CreateTimer(static s => CancelQuietly((CancellationTokenSource)s!), poll, PausePoll, Timeout.InfiniteTimeSpan))
        {
            return gate.WaitOpen(Timeout.InfiniteTimeSpan, poll.Token) || gate.IsOpen;
        }
    }

    // A timer callback can still run after its timer (and this source) were disposed.
    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The wait already ended.
        }
    }
}

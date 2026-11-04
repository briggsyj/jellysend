using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Slopsoil.Discord.Native;
using Slopsoil.Streaming.Contracts;

namespace Slopsoil.Streaming.Audio;

/// <summary>Sends one encoded 20 ms Opus frame (normally <see cref="RtpMediaSender.SendOpus"/>).</summary>
public delegate void OpusSink(ReadOnlySpan<byte> opus);

/// <summary>Underrun handling so far: silence sent for empty slots, late PCM frames dropped.</summary>
public readonly record struct AudioCorrections(long Silence, long Dropped);

/// <summary>
/// Go-live audio sender (spec 004): a reader thread buffers FFmpeg's PCM ahead of playout
/// (<see cref="PcmReadAhead"/>); the sender thread encodes each 20 ms frame to Opus and sends frame <c>k</c> of a
/// segment at <c>origin + k · 20 ms − STREAM_AV_SYNC_MS</c> on the video player's playout clock
/// (<see cref="IMediaClock.Anchor"/>), so audio and video share one clock and no drift corrections exist (FR-004,
/// FR-006). An empty buffer at a slot sends silence and the late frame is dropped when it arrives, so audio never
/// falls behind the clock (FR-005). A pause holds both streams (the player shifts the origin); a seek, aspect or
/// debug restart discards the old segment's audio and re-anchors on the new segment (FR-007).
/// Without a clock the sender plays from its own origin (tests).
/// </summary>
public sealed class AudioSender
{
    /// <summary>Audio buffered beyond the video's read-ahead: FFmpeg emits audio a little ahead of video.</summary>
    public static readonly TimeSpan Headroom = TimeSpan.FromSeconds(1);

    private const int FrameBytes = OpusEncoder.FrameBytes;
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan PausePoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AnchorPoll = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan MinPacingWait = TimeSpan.FromMilliseconds(1);

    /// <summary>A frame this late (the sender thread was descheduled) is dropped instead of sent in a catch-up burst.</summary>
    internal static readonly TimeSpan MaxLateness = TimeSpan.FromMilliseconds(200);

    private readonly IPcmSource _pcm;
    private readonly IMediaClock? _clock;
    private readonly IAudioStats? _stats;
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
    private long _silenceSent;
    private long _dropped;
    private volatile PcmReadAhead? _reader;

    /// <summary>Send through a go-live connection's <see cref="RtpMediaSender"/> (RTP, DAVE, transport crypto).</summary>
    public AudioSender(
        IPcmSource pcm,
        IMediaClock? clock,
        IAudioStats? stats,
        RtpMediaSender sender,
        int avSyncMs,
        ILogger? logger = null,
        TimeProvider? time = null,
        Func<IAudioFrameEncoder>? encoderFactory = null,
        double bufferSeconds = 0)
        : this(pcm, clock, stats, (sender ?? throw new ArgumentNullException(nameof(sender))).SendOpus, avSyncMs, logger, time, encoderFactory, bufferSeconds)
    {
    }

    /// <param name="pcm">FFmpeg's PCM output (the audio FIFO).</param>
    /// <param name="clock">The video player's playout clock; null plays from the sender's own origin.</param>
    /// <param name="stats">Diagnostics sink for drift, buffer fill and underrun steps.</param>
    /// <param name="send">Where encoded frames go.</param>
    /// <param name="avSyncMs"><c>STREAM_AV_SYNC_MS</c>: positive advances audio, negative delays it (±5000).</param>
    /// <param name="logger">Defaults to a null logger.</param>
    /// <param name="time">Pacing clock; must be the video player's (defaults to <see cref="TimeProvider.System"/>).</param>
    /// <param name="encoderFactory">Created on the sender thread; defaults to libopus.</param>
    /// <param name="bufferSeconds"><c>STREAM_BUFFER_SECONDS</c>: PCM read ahead (plus <see cref="Headroom"/>).</param>
    public AudioSender(
        IPcmSource pcm,
        IMediaClock? clock,
        IAudioStats? stats,
        OpusSink send,
        int avSyncMs,
        ILogger? logger = null,
        TimeProvider? time = null,
        Func<IAudioFrameEncoder>? encoderFactory = null,
        double bufferSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentNullException.ThrowIfNull(send);
        _pcm = pcm;
        _clock = clock;
        _stats = stats;
        _send = send;
        _avSyncMs = Math.Clamp(avSyncMs, -5000, 5000);
        _log = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _encoderFactory = encoderFactory ?? (static () => new OpusFrameEncoder());
        BufferCapacity = (int)Math.Ceiling((TimeSpan.FromSeconds(Math.Clamp(bufferSeconds, 0.0, 30.0)) + Headroom) / FrameDuration);
        _handle = clock?.RegisterAudioFollower();
    }

    /// <summary>Completes (never faults) once the sender has stopped: Stop, end of input, or an error (logged).</summary>
    public Task Completion => _completion.Task;

    /// <summary>Underrun steps so far (thread-safe).</summary>
    public AudioCorrections Corrections => new(Interlocked.Read(ref _silenceSent), Interlocked.Read(ref _dropped));

    /// <summary>Opus frames handed to the sink.</summary>
    public long FramesSent => Interlocked.Read(ref _framesSent);

    /// <summary>Most 20 ms PCM frames read ahead.</summary>
    public int BufferCapacity { get; }

    /// <summary>20 ms PCM frames buffered right now.</summary>
    internal int BufferedFrames => _reader?.Count ?? 0;

    /// <summary>The read-ahead is not about to buffer more on its own (ended, between writers, or full).</summary>
    internal bool ReadAheadSettled => _reader?.Settled ?? false;

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
        _reader?.Stop();
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
        // Don't leave the player waiting on a sender that's gone.
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

    /// <summary>The sender thread's body; runs synchronously on the caller in tests.</summary>
    internal void SendLoop()
    {
        Interlocked.CompareExchange(ref _state, 1, 0); // running: Stop no longer finishes on its own
        var clock = _clock;
        var reader = new PcmReadAhead(_pcm, BufferCapacity, () => clock?.IsSourceActive == true, _log);
        _reader = reader;
        reader.Start();
        if (Stopping)
        {
            reader.Stop();
        }

        try
        {
            using var encoder = _encoderFactory();
            _log.LogInformation(
                "GoLiveAudio: starting audio transmission (av_sync_ms={AvSyncMs}, buffer {Frames} frames)", _avSyncMs, BufferCapacity);
            Play(encoder, reader, clock);
        }
        finally
        {
            reader.Stop();
            if (!reader.Join(TimeSpan.FromSeconds(1)))
            {
                _log.LogDebug("GoLiveAudio: PCM reader still blocked in a read; abandoning it");
            }

            _stats?.AudioBuffered(TimeSpan.Zero);
        }
    }

    private void Play(IAudioFrameEncoder encoder, PcmReadAhead reader, IMediaClock? clock)
    {
        var seq = clock?.RestartSeq ?? 0;
        PlayoutAnchor? own = null; // the origin when there is no clock
        long slot = 0;             // next 20 ms slot of the segment
        long consumed = 0;         // PCM frames of the segment taken from the buffer (sent or dropped)
        long sentInSegment = 0;
        var avSyncTicks = (long)(_avSyncMs / 1000.0 * _time.TimestampFrequency);
        var frameTicks = (long)(FrameDuration.TotalSeconds * _time.TimestampFrequency);

        while (!Stopping)
        {
            PlayoutAnchor anchor;
            if (clock is null)
            {
                if (own is null)
                {
                    // Own origin once the read-ahead has filled (or the input ended): deterministic for tests.
                    if (!reader.Settled)
                    {
                        _stop.Token.WaitHandle.WaitOne(1);
                        continue;
                    }

                    own = new PlayoutAnchor(0, _time.GetTimestamp());
                }

                anchor = own;
            }
            else
            {
                if (clock.RestartSeq != seq)
                {
                    // FFmpeg restarted: drop the old segment's audio, then let the player start the new one (FR-007).
                    seq = clock.RestartSeq;
                    var cleared = reader.BeginDrain(_handle is null ? null : _handle.SignalDrained);
                    _log.LogInformation(
                        "GoLiveAudio: source restarted at {Offset:F1}s — dropped {Frames} buffered frames", clock.SegmentOffset, cleared);
                    (slot, consumed, sentInSegment) = (0, 0, 0);
                    continue;
                }

                if (clock.SeekPending)
                {
                    // The old FFmpeg is going away: its audio is stale. Keep reading it so FFmpeg never blocks on the FIFO.
                    reader.BeginDrain(null);
                    if (WaitStop(AnchorPoll))
                    {
                        return;
                    }

                    continue;
                }

                if (!clock.Pause.IsOpen)
                {
                    WaitPauseOpen(clock.Pause); // the player moves the origin by the pause
                    continue;
                }

                if (clock.Anchor is not { } current || current.Segment != seq)
                {
                    // Pre-roll or restart: the segment's first frame is not due yet.
                    if (reader.Ended && reader.Count == 0)
                    {
                        return;
                    }

                    if (WaitStop(AnchorPoll))
                    {
                        return;
                    }

                    continue;
                }

                anchor = current;
            }

            if (reader.Ended && reader.Count == 0)
            {
                return; // end of input: nothing more to play
            }

            var due = anchor.Origin + (slot * frameTicks) - avSyncTicks;
            var now = _time.GetTimestamp();
            var early = TimeSpan.FromSeconds((double)(due - now) / _time.TimestampFrequency);
            if (early > MinPacingWait)
            {
                // Wait for the slot, then look at the clock again (pause, restart).
                if (WaitStop(early))
                {
                    return;
                }

                continue;
            }

            if (-early >= (sentInSegment == 0 ? FrameDuration : MaxLateness))
            {
                // The slot is long gone (positive av-sync at a segment start, or a thread stalled for longer than
                // MaxLateness): skip it, and drop its frame now or when it arrives. Shorter stalls send late, in a
                // burst, and so catch up without losing audio.
                if (consumed == slot && reader.TryTake(_frame))
                {
                    consumed++;
                    CountDrop(sentInSegment);
                }

                slot++;
                continue;
            }

            // Pay the debt of earlier underruns: those frames' slots are gone (FR-005).
            while (consumed < slot && reader.TryTake(_frame))
            {
                consumed++;
                CountDrop(sentInSegment);
            }

            byte[] pcm;
            if (consumed == slot && reader.TryTake(_frame))
            {
                consumed++;
                pcm = _frame;
            }
            else if (reader.InGap || reader.Ended)
            {
                // This segment's audio is over (the writer closed the FIFO): nothing more will come for it.
                if (reader.Ended && reader.Count == 0)
                {
                    return;
                }

                if (WaitStop(AnchorPoll))
                {
                    return;
                }

                continue;
            }
            else
            {
                // Underrun: keep the slot with silence; the late frame is dropped when it arrives.
                pcm = _silence;
                Interlocked.Increment(ref _silenceSent);
                _stats?.AudioSilenceStep();
            }

            _send(encoder.Encode(pcm));
            Interlocked.Increment(ref _framesSent);
            sentInSegment++;
            slot++;
            ReportStats(clock, anchor, due, reader);
        }
    }

    private void CountDrop(long sentInSegment)
    {
        if (sentInSegment == 0)
        {
            return; // positive av-sync discarding the segment's first frames: by design, not a correction
        }

        Interlocked.Increment(ref _dropped);
        _stats?.AudioSkipStep();
    }

    /// <summary>
    /// Drift = how far video is behind its schedule minus how far audio is (FR-006), both on the shared clock and
    /// with the av-sync offset already in the audio schedule; plus the buffer fill.
    /// </summary>
    private void ReportStats(IMediaClock? clock, PlayoutAnchor anchor, long audioDue, PcmReadAhead reader)
    {
        if (_stats is null)
        {
            return;
        }

        _stats.AudioBuffered(reader.Count * FrameDuration);
        if (clock is null || clock.Fps <= 0)
        {
            return;
        }

        var now = _time.GetTimestamp();
        var freq = (double)_time.TimestampFrequency;
        var videoDue = anchor.Origin + (long)(clock.SegmentFrames / clock.Fps * freq);
        var videoBehind = Math.Max(0.0, (now - videoDue) / freq);
        var audioBehind = Math.Max(0.0, (now - audioDue) / freq);
        _stats.AudioDrift((videoBehind - audioBehind) * 1000.0);
    }

    /// <summary>Wait on the stop signal (Python's <c>_end.wait</c>): true if a stop has been requested.</summary>
    private bool WaitStop(TimeSpan timeout)
    {
        if (Stopping)
        {
            return true;
        }

        if (ReferenceEquals(_time, TimeProvider.System))
        {
            // A wait handle, not a thread-pool timer callback: a busy pool or GC pause must not stall the 20 ms
            // pacing and then burst (VideoPlayer.Sleep does the same; Python used Event.wait).
            _stop.Token.WaitHandle.WaitOne(timeout);
            return Stopping;
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
        if (ReferenceEquals(_time, TimeProvider.System))
        {
            return gate.WaitOpen(PausePoll, _stop.Token) || gate.IsOpen;
        }

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

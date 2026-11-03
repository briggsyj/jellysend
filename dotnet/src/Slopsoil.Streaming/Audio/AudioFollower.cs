using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Slopsoil.Streaming.Contracts;

namespace Slopsoil.Streaming.Audio;

/// <summary>What the audio sender does with this 20 ms tick.</summary>
internal enum FollowAction
{
    /// <summary>Read one PCM frame and play it.</summary>
    Play,

    /// <summary>Send silence instead; PCM is not consumed (the step is already counted).</summary>
    Silence,

    /// <summary>Discard one PCM frame (call <see cref="AudioFollower.CommitSkip"/> if it was whole), then read and play the next.</summary>
    Skip,
}

/// <summary>
/// The A/V clock follower of <c>golive.GoLiveAudioSender</c> (<c>_follow_video</c>, <c>_correct</c>,
/// <c>_check_restart</c> state), without any I/O, so every rule of FR-054 is testable deterministically.
/// Single-threaded: only the sender thread mutates it; the correction counters may be read from anywhere.
/// </summary>
internal sealed class AudioFollower
{
    /// <summary>Base dead band (s): two frames of slack against normal jitter.</summary>
    public const double SyncSlack = 0.04;

    /// <summary>Settling window after a segment's first frame (s).</summary>
    public const double SettleSeconds = 1.0;

    /// <summary>Corrections allowed while settling (≤ 300 ms).</summary>
    public const int MaxSettleSteps = 15;

    /// <summary>Corrections per persistent-drift episode.</summary>
    public const int MaxEpisodeSteps = 15;

    /// <summary>Net silence (silence minus skips) per segment: the PCM FIFO only buffers ~0.34 s (300 ms).</summary>
    public const int MaxNetSilence = 15;

    /// <summary>Drift (s) above which steady-state drift tracking starts.</summary>
    public const double DriftEnter = 0.12;

    /// <summary>Drift (s) within which an episode ends.</summary>
    public const double DriftExit = 0.03;

    /// <summary>How long (s) drift must stay above <see cref="DriftEnter"/> before correcting.</summary>
    public const double DriftPersist = 1.0;

    /// <summary>Minimum time (s) between steady-state corrections.</summary>
    public const double CorrectionInterval = 0.25;

    /// <summary>One Opus frame (s).</summary>
    public const double FrameDuration = 0.020;

    private readonly IAudioStats? _stats;
    private readonly ILogger _log;
    private long _silence;
    private long _dropped;

    public AudioFollower(double fps, int restartSeq, double segmentOffset, IAudioStats? stats = null, ILogger? logger = null)
    {
        _stats = stats;
        _log = logger ?? NullLogger.Instance;
        SeenSeq = restartSeq;
        SegmentBase = segmentOffset;
        // Position moves in 1/fps steps; keep the dead band wider than a step so low frame
        // rates don't flip-flop between silence and drops.
        Slack = Math.Max(SyncSlack, fps > 0 ? (0.5 / fps) + 0.02 : 0.0);
    }

    public double Slack { get; }

    public int SeenSeq { get; private set; }

    /// <summary>Media position the current segment's audio started at.</summary>
    internal double SegmentBase { get; set; }

    /// <summary>20 ms frames consumed since the last restart.</summary>
    internal int SegmentFrames { get; set; }

    internal int SettleSteps { get; set; }

    internal int EpisodeSteps { get; set; }

    internal int NetSilence { get; set; }

    internal double? DriftSince { get; set; }

    internal bool Correcting { get; set; }

    internal double LastCorrection { get; private set; } = double.NegativeInfinity;

    public long SilenceSteps => Interlocked.Read(ref _silence);

    public long DroppedSteps => Interlocked.Read(ref _dropped);

    public double AudioPosition => SegmentBase + (SegmentFrames * FrameDuration);

    /// <summary>A PCM frame was read and played.</summary>
    public void FrameConsumed() => SegmentFrames++;

    /// <summary>Re-anchor on a new FFmpeg segment and forget all per-segment state.</summary>
    public void Rebase(int restartSeq, double segmentOffset)
    {
        SeenSeq = restartSeq;
        SegmentBase = segmentOffset;
        SegmentFrames = 0;
        SettleSteps = 0;
        EpisodeSteps = 0;
        NetSilence = 0;
        ResetPersistence();
    }

    /// <summary>Persistence must be observed while playing, not across a pause.</summary>
    public void ResetPersistence()
    {
        DriftSince = null;
        Correcting = false;
    }

    /// <summary>Waiting for the segment's first video frame: true (and budgeted) if silence may be sent.</summary>
    public bool TryWaitSilence()
    {
        if (NetSilence >= MaxNetSilence)
        {
            return false;
        }

        NetSilence++;
        return true;
    }

    /// <summary>Decide this tick while video is flowing. <paramref name="now"/> is monotonic seconds.</summary>
    public FollowAction Follow(IMediaClock clock, double target, double now)
    {
        var fps = clock.Fps;
        var videoPos = clock.Position;
        if (fps > 0)
        {
            // Position steps at each sent frame, so it leads wall time by ~half a frame on average.
            videoPos -= 0.5 / fps;
        }

        var drift = AudioPosition - videoPos - target;
        _stats?.AudioDrift(drift * 1000.0);

        // (a) Settling window: align quickly after the segment's first frame.
        if (fps > 0 && clock.SegmentFrames < fps * SettleSeconds)
        {
            if (Math.Abs(drift) > Slack && SettleSteps < MaxSettleSteps)
            {
                SettleSteps++;
                return Correct(drift);
            }

            return FollowAction.Play;
        }

        // (b) Steady playback: only large, persistent drift, gently and capped.
        if (Math.Abs(drift) <= DriftExit)
        {
            ResetPersistence();
            EpisodeSteps = 0;
            return FollowAction.Play;
        }

        if (Math.Abs(drift) > DriftEnter)
        {
            if (DriftSince is not { } since)
            {
                DriftSince = now;
            }
            else if (now - since >= DriftPersist)
            {
                Correcting = true;
            }
        }
        else if (!Correcting)
        {
            DriftSince = null;
        }

        if (!Correcting || now - LastCorrection < CorrectionInterval)
        {
            return FollowAction.Play;
        }

        if (EpisodeSteps >= MaxEpisodeSteps)
        {
            // End this episode; a new one may start after fresh persistence.
            _log.LogWarning("GoLiveAudio: {DriftMs:F0} ms drift left after a correction episode", drift * 1000.0);
            ResetPersistence();
            EpisodeSteps = 0;
            return FollowAction.Play;
        }

        LastCorrection = now;
        EpisodeSteps++;
        return Correct(drift);
    }

    /// <summary>One 20 ms step: silence if audio is ahead, skip a frame if behind.</summary>
    public FollowAction Correct(double drift)
    {
        if (drift > 0)
        {
            if (NetSilence >= MaxNetSilence)
            {
                return FollowAction.Play; // FIFO budget spent: leave the remainder
            }

            NetSilence++;
            Interlocked.Increment(ref _silence);
            _stats?.AudioSilenceStep();
            return FollowAction.Silence;
        }

        return FollowAction.Skip;
    }

    /// <summary>The frame a <see cref="FollowAction.Skip"/> asked for was read in full.</summary>
    public void CommitSkip()
    {
        SegmentFrames++;
        NetSilence = Math.Max(0, NetSilence - 1);
        Interlocked.Increment(ref _dropped);
        _stats?.AudioSkipStep();
    }
}

using Microsoft.Extensions.Logging;
using Slopsoil.Streaming.Audio;
using Slopsoil.Streaming.Contracts;

namespace Slopsoil.Tests.Streaming;

/// <summary>The pure follower core (<c>_follow_video</c> / <c>_correct</c>) driven tick by tick (FR-054).</summary>
public sealed class AudioSenderFollowerTests
{
    private static AudioFollower Follower(double fps = 0, IAudioStats? stats = null, ILogger? log = null) =>
        new(fps, 0, 0, stats, log);

    [Fact]
    public void Constants_match_the_python_follower()
    {
        Assert.Equal(0.04, AudioFollower.SyncSlack);
        Assert.Equal(1.0, AudioFollower.SettleSeconds);
        Assert.Equal(15, AudioFollower.MaxSettleSteps);
        Assert.Equal(15, AudioFollower.MaxEpisodeSteps);
        Assert.Equal(15, AudioFollower.MaxNetSilence);
        Assert.Equal(0.12, AudioFollower.DriftEnter);
        Assert.Equal(0.03, AudioFollower.DriftExit);
        Assert.Equal(1.0, AudioFollower.DriftPersist);
        Assert.Equal(0.25, AudioFollower.CorrectionInterval);
    }

    [Theory]
    [InlineData(30.0, 0.04)]
    [InlineData(10.0, 0.07)]
    [InlineData(1.0, 0.52)]
    [InlineData(0.0, 0.04)]
    public void Slack_widens_for_low_frame_rates(double fps, double expected)
    {
        var clock = new FakeMediaClock { Fps = fps };
        var sender = new AudioSender(FakePcm.Frames(0), clock, null, _ => { }, 0);
        Assert.Equal(expected, sender.Follower.Slack, 9);
    }

    [Fact]
    public void Drift_inside_exit_band_resets_tracking()
    {
        var f = Follower();
        f.DriftSince = 5.0;
        f.Correcting = true;
        f.EpisodeSteps = 3;
        f.SegmentBase = 0.01; // 10 ms drift
        Assert.Equal(FollowAction.Play, f.Follow(new FakeMediaClock(), 0.0, 10.0));
        Assert.Null(f.DriftSince);
        Assert.False(f.Correcting);
        Assert.Equal(0, f.EpisodeSteps);
    }

    [Fact]
    public void Moderate_drift_between_bands_drops_tracking_unless_correcting()
    {
        var f = Follower();
        var clock = new FakeMediaClock();
        f.SegmentBase = 0.08; // between exit (30 ms) and enter (120 ms)
        f.DriftSince = 1.0;
        Assert.Equal(FollowAction.Play, f.Follow(clock, 0.0, 5.0));
        Assert.Null(f.DriftSince);

        // While correcting, the episode keeps going until the exit band.
        f.DriftSince = 1.0;
        f.Correcting = true;
        Assert.Equal(FollowAction.Silence, f.Follow(clock, 0.0, 5.0));
        Assert.Equal(1.0, f.DriftSince);
        Assert.Equal(1, f.EpisodeSteps);
    }

    [Fact]
    public void Persistent_episode_is_capped()
    {
        var f = Follower();
        var clock = new FakeMediaClock();
        f.SegmentBase = 1.0; // 1 s ahead, never converges in this fake
        var steps = 0;
        var t = 0.0;
        for (var i = 0; i < 200; i++)
        {
            t += 0.3;
            if (f.Follow(clock, 0.0, t) != FollowAction.Play)
            {
                steps++;
            }
        }

        Assert.Equal(AudioFollower.MaxEpisodeSteps, steps);
    }

    [Fact]
    public void Episode_cap_ends_episode_instead_of_disabling()
    {
        var log = new ListLogger();
        var f = Follower(log: log);
        var clock = new FakeMediaClock();
        f.SegmentBase = -2.0; // audio 2 s behind: skips (FIFO-safe)
        var t = 0.0;
        for (var i = 0; i < 400; i++)
        {
            t += 0.3;
            if (f.Follow(clock, 0.0, t) == FollowAction.Skip && f.SegmentFrames < 100)
            {
                f.CommitSkip(); // the fake FIFO holds 100 frames, like the Python test
            }
        }

        // Several episodes ran (each capped), rather than stopping after the first.
        Assert.True(f.DroppedSteps > AudioFollower.MaxEpisodeSteps);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("drift left after a correction episode"));
    }

    [Fact]
    public void Silence_respects_net_budget()
    {
        var f = Follower();
        f.NetSilence = AudioFollower.MaxNetSilence;
        Assert.Equal(FollowAction.Play, f.Correct(0.5)); // audio ahead but budget spent
        Assert.Equal(0, f.SilenceSteps);
    }

    [Fact]
    public void Skips_refund_the_silence_budget()
    {
        var stats = new FakeAudioStats();
        var f = Follower(stats: stats);
        f.NetSilence = 3;
        Assert.Equal(FollowAction.Skip, f.Correct(-0.5));
        f.CommitSkip();
        Assert.Equal((2, 1L, 1, 1), (f.NetSilence, f.DroppedSteps, f.SegmentFrames, stats.Skips));

        f.NetSilence = 0;
        f.CommitSkip();
        Assert.Equal(0, f.NetSilence); // never negative
    }

    [Fact]
    public void Position_quantization_bias_removed()
    {
        var f = Follower(10.0);
        var clock = new FakeMediaClock { PositionFn = () => 1.0, Fps = 10.0, SegmentFrames = 100 };
        f.SegmentBase = 0.95; // in step with the de-biased video clock
        var t = 0.0;
        for (var i = 0; i < 20; i++)
        {
            t += 0.3;
            Assert.Equal(FollowAction.Play, f.Follow(clock, 0.0, t));
        }
    }

    [Fact]
    public void Settling_corrects_immediately_up_to_the_cap_then_leaves_drift()
    {
        var f = Follower(30.0);
        var clock = new FakeMediaClock { PositionFn = () => -1.0, Fps = 30.0, SegmentFrames = 1 };
        for (var i = 0; i < AudioFollower.MaxSettleSteps; i++)
        {
            Assert.Equal(FollowAction.Silence, f.Follow(clock, 0.0, 0.0)); // no persistence wait
        }

        Assert.Equal(FollowAction.Play, f.Follow(clock, 0.0, 0.0));
        Assert.Equal(AudioFollower.MaxSettleSteps, f.SettleSteps);
        Assert.Equal(AudioFollower.MaxSettleSteps, f.SilenceSteps);
    }

    [Fact]
    public void Settling_window_ignores_drift_inside_the_slack_and_ends_after_one_second()
    {
        var f = Follower(30.0);
        var clock = new FakeMediaClock { PositionFn = () => 0.0, Fps = 30.0, SegmentFrames = 29 };
        f.SegmentBase = 0.02; // 20 ms + 1/60 s bias = ~37 ms < 40 ms slack
        Assert.Equal(FollowAction.Play, f.Follow(clock, 0.0, 0.0));

        f.SegmentBase = 0.5;
        Assert.Equal(FollowAction.Silence, f.Follow(clock, 0.0, 0.0));
        clock.SegmentFrames = 30; // 1 s of video: steady state, no immediate correction
        Assert.Equal(FollowAction.Play, f.Follow(clock, 0.0, 0.0));
        Assert.Equal(1, f.SettleSteps);
    }

    [Fact]
    public void Steady_state_correction_needs_persistence_then_rate_limits()
    {
        var f = Follower();
        var clock = new FakeMediaClock { PositionFn = () => 0.0 };
        f.SegmentBase = 0.5;
        Assert.Equal(FollowAction.Play, f.Follow(clock, 0.0, 10.0)); // starts the persistence timer
        Assert.Equal(10.0, f.DriftSince);
        Assert.Equal(FollowAction.Play, f.Follow(clock, 0.0, 10.99));
        Assert.Equal(FollowAction.Silence, f.Follow(clock, 0.0, 11.0));
        Assert.Equal(FollowAction.Play, f.Follow(clock, 0.0, 11.24)); // rate limit
        Assert.Equal(FollowAction.Silence, f.Follow(clock, 0.0, 11.25));
        Assert.Equal(11.25, f.LastCorrection);
        Assert.Equal(2, f.EpisodeSteps);
    }

    [Fact]
    public void Drift_is_reported_in_milliseconds_with_target_removed()
    {
        var stats = new FakeAudioStats();
        var f = Follower(stats: stats);
        f.SegmentBase = 0.25;
        f.Follow(new FakeMediaClock { PositionFn = () => 0.1 }, 0.05, 0.0);
        Assert.Equal(100.0, Assert.Single(stats.Drifts), 6);
    }

    [Fact]
    public void Rebase_resets_all_segment_state()
    {
        var f = Follower();
        f.SegmentFrames = 7;
        f.SettleSteps = 9;
        f.EpisodeSteps = 4;
        f.NetSilence = 5;
        f.DriftSince = 1.0;
        f.Correcting = true;
        f.Rebase(3, 42.0);
        Assert.Equal((3, 42.0, 0, 0, 0, 0, false), (f.SeenSeq, f.SegmentBase, f.SegmentFrames, f.SettleSteps, f.EpisodeSteps, f.NetSilence, f.Correcting));
        Assert.Null(f.DriftSince);
        Assert.Equal(42.0, f.AudioPosition);
    }

    [Fact]
    public void Wait_silence_is_budgeted()
    {
        var f = Follower();
        for (var i = 0; i < AudioFollower.MaxNetSilence; i++)
        {
            Assert.True(f.TryWaitSilence());
        }

        Assert.False(f.TryWaitSilence());
    }
}

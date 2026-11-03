using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Slopsoil.Discord.Rtp;
using Slopsoil.Streaming;
using Slopsoil.Streaming.Audio;
using Slopsoil.Streaming.Contracts;

namespace Slopsoil.Tests.Streaming;

/// <summary>Port of the <c>GoLiveAudioSender</c> cases in <c>tests/test_golive.py</c> (T027, T033, T048).</summary>
public sealed class AudioSenderTests
{
    private const int Frame = FakePcm.Frame;
    private const int Overhead = RtpHeader.Size + 16 + 4; // header + AEAD tag + nonce suffix

    private readonly StepTimeProvider _time = new();
    private readonly FakeEncoder _encoder = new();
    private readonly List<byte[]> _sent = [];
    private Action? _afterSend;

    private AudioSender Sender(IPcmSource pcm, FakeMediaClock? clock = null, int avSyncMs = 0, IAudioStats? stats = null, ILogger? log = null) =>
        new(pcm, clock, stats, Sink, avSyncMs, log, _time, () => _encoder);

    private void Sink(ReadOnlySpan<byte> opus)
    {
        _sent.Add(opus.ToArray());
        _afterSend?.Invoke();
    }

    private List<int> PayloadIds() => _sent.Select(p => (int)p[^1]).ToList();

    /// <summary>A clock that tracks the sender's own audio position (Python's <c>position=lambda: s._audio_position()</c>).</summary>
    private static FakeMediaClock InStep(Func<AudioSender?> sender, double offset = 0.0) =>
        new() { PositionFn = () => (sender()?.AudioPosition ?? 0.0) + offset };

    private static Func<double> Ticks(params double[] ticks)
    {
        var i = 0;
        return () => ticks[i++];
    }

    private static Func<double> Ticking(double step)
    {
        var t = 0.0;
        return () => t += step;
    }

    // ── basic sending ────────────────────────────────────────────────────────

    [Fact]
    public void Sends_rtp_packets_through_the_media_sender()
    {
        var datagrams = new List<byte[]>();
        var rtp = new RtpMediaSender(d =>
        {
            datagrams.Add(d.ToArray());
            return true;
        }, TransportCrypto.XChaChaRtpSize, new byte[32], 0x1234, 0x1235, () => null);
        var sender = new AudioSender(FakePcm.Frames(3, extra: 100), null, null, rtp, 0, null, _time, () => _encoder);

        sender.SendLoop();

        Assert.Equal(3, datagrams.Count);
        for (var i = 0; i < 3; i++)
        {
            var p = datagrams[i];
            Assert.Equal((0x80, 0x78), (p[0], p[1]));
            Assert.Equal(i, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2)));
            Assert.Equal((uint)(i * 960), BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4)));
            Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(8)));
            Assert.Equal((uint)i, BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(p.Length - 4)));
            Assert.Equal(5 + Overhead, p.Length);
        }

        Assert.Equal(3, sender.FramesSent);
        Assert.Equal(3, _encoder.Calls);
        Assert.True(_encoder.Disposed);
    }

    [Fact]
    public void Real_opus_frames_are_compressed()
    {
        var datagrams = new List<byte[]>();
        var rtp = new RtpMediaSender(d =>
        {
            datagrams.Add(d.ToArray());
            return true;
        }, TransportCrypto.XChaChaRtpSize, new byte[32], 1, 2, () => null);
        var sender = new AudioSender(FakePcm.Frames(2), null, null, rtp, 0, null, _time);

        sender.SendLoop();

        Assert.Equal(2, datagrams.Count);
        Assert.InRange(datagrams[0].Length - Overhead, 1, Frame - 1);
    }

    [Fact]
    public void Media_sender_overload_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => new AudioSender(FakePcm.Frames(0), null, null, (RtpMediaSender)null!, 0));
        Assert.Throws<ArgumentNullException>(() => new AudioSender(null!, null, null, _ => { }, 0));
        Assert.Throws<ArgumentNullException>(() => new AudioSender(FakePcm.Frames(0), null, null, (OpusSink)null!, 0));
    }

    // ── STREAM_AV_SYNC_MS ────────────────────────────────────────────────────

    [Fact]
    public void Positive_av_sync_discards_backlog()
    {
        Sender(FakePcm.Frames(5), avSyncMs: 40).SendLoop(); // 2 frames
        Assert.Equal([2, 3, 4], PayloadIds());
    }

    [Fact]
    public void Positive_av_sync_stops_at_eof()
    {
        Sender(FakePcm.Frames(2), avSyncMs: 1000).SendLoop();
        Assert.Empty(_sent);
    }

    [Fact]
    public void Positive_av_sync_stops_discarding_on_stop()
    {
        var s = Sender(FakePcm.Frames(5), avSyncMs: 60);
        s.Stop();
        s.SendLoop();
        Assert.Empty(_sent);
        Assert.Equal(0, s.Follower.SegmentFrames);
    }

    [Fact]
    public void Negative_av_sync_delays_start()
    {
        Sender(FakePcm.Frames(1), avSyncMs: -250).SendLoop();
        Assert.Equal(TimeSpan.FromMilliseconds(250), _time.Waits[0]);
        Assert.Single(_sent);
    }

    [Fact]
    public void Av_sync_is_clamped_to_five_seconds()
    {
        Sender(FakePcm.Frames(1), avSyncMs: -9000).SendLoop();
        Assert.Equal(TimeSpan.FromSeconds(5), _time.Waits[0]);
    }

    [Fact]
    public void Target_offset_from_av_sync_ms_is_kept()
    {
        // +40 discards 2 frames up front and the follower keeps audio 40 ms ahead instead of
        // "correcting" it back.
        AudioSender? s = null;
        var clock = InStep(() => s, -0.04);
        s = Sender(FakePcm.Frames(5), clock, avSyncMs: 40);
        s.SendLoop();
        Assert.Equal([2, 3, 4], PayloadIds());
        Assert.Equal(new AudioCorrections(0, 0), s.Corrections);
    }

    // ── short reads, stop, pacing ────────────────────────────────────────────

    [Fact]
    public void Waits_through_fallback_gap()
    {
        // A short read while the source is still active is a FIFO gap, not EOF.
        var pcm = new FakePcm([Enumerable.Repeat((byte)1, Frame).ToArray(), [], [], Enumerable.Repeat((byte)2, Frame).ToArray(), []]);
        var active = new Queue<bool>([true, true, false]);
        AudioSender? s = null;
        var clock = InStep(() => s);
        clock.SourceActive = active.Dequeue;
        s = Sender(pcm, clock);

        s.SendLoop();

        Assert.Equal([1, 2], PayloadIds());
        Assert.Equal(2, _time.WaitsOf(50));
    }

    [Fact]
    public void Short_read_without_a_clock_ends_the_stream()
    {
        Sender(new FakePcm([new byte[100]])).SendLoop();
        Assert.Empty(_sent);
    }

    [Fact]
    public void Stop_during_gap()
    {
        var clock = new FakeMediaClock { SourceActive = () => true };
        var s = Sender(FakePcm.Frames(0), clock);
        _time.OnWait = _ => s.Stop(); // stop arrives while waiting
        s.SendLoop();
        Assert.Empty(_sent);
        Assert.Equal(1, _time.WaitsOf(50));
    }

    [Fact]
    public void Stop_breaks_loop()
    {
        var s = Sender(FakePcm.Frames(10));
        _afterSend = s.Stop;
        s.SendLoop();
        Assert.Single(_sent);
    }

    [Fact]
    public void Stop_is_seen_even_when_no_pacing_wait_is_due()
    {
        var s = Sender(FakePcm.Frames(10));
        _afterSend = () =>
        {
            _time.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(30), Timeout.InfiniteTimeSpan); // run late
            s.Stop();
        };
        s.SendLoop();
        Assert.Single(_sent);
    }

    [Fact]
    public void Paces_to_an_absolute_schedule()
    {
        // Freeze the clock so every frame is "early" and must wait for its slot.
        _time.AdvanceOnWait = false;
        Sender(FakePcm.Frames(3)).SendLoop();
        Assert.Equal([20, 40, 60], _time.Waits.Select(w => (int)w.TotalMilliseconds));
    }

    [Fact]
    public void Late_frames_do_not_wait()
    {
        var s = Sender(FakePcm.Frames(3));
        _afterSend = () => _time.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);
        s.SendLoop();
        Assert.Equal(3, _sent.Count);
        Assert.Equal(3, _time.WaitsOf(100)); // only the simulated stalls; no pacing waits
    }

    // ── thread, completion, errors ───────────────────────────────────────────

    [Fact]
    public async Task Thread_runs_to_end_of_input_and_completes()
    {
        string? threadName = null;
        var s = Sender(FakePcm.Frames(2));
        _afterSend = () => threadName = Thread.CurrentThread.Name;
        s.Start();
        s.Start(); // once only
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, _sent.Count);
        Assert.Equal("GoLiveAudio", threadName);
    }

    [Fact]
    public async Task Run_logs_errors_and_still_completes()
    {
        var log = new ListLogger();
        var clock = new FakeMediaClock();
        var s = new AudioSender(FakePcm.Frames(1), clock, null, Sink, 0, log, _time, () => throw new InvalidOperationException("no opus"));
        s.Start();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message == "GoLiveAudioSender error" && e.Error is InvalidOperationException);
        Assert.Contains(log.Entries, e => e.Message == "GoLiveAudioSender stopped");
        Assert.True(clock.Handles[0].Disposed);
    }

    [Fact]
    public async Task Sender_exit_unregisters_follower()
    {
        var clock = new FakeMediaClock();
        var s = Sender(FakePcm.Frames(0), clock);
        Assert.Single(clock.Handles);
        s.Start(); // EOF straight away
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var h = clock.Handles[0];
        Assert.True(h.Disposed);
        Assert.True(h.SignaledBeforeDispose);
    }

    [Fact]
    public async Task Stop_before_start_completes_and_unregisters_and_is_idempotent()
    {
        var clock = new FakeMediaClock();
        var s = Sender(FakePcm.Frames(3), clock);
        s.Stop();
        s.Stop();
        s.Start(); // no-op after stop
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(_sent);
        Assert.True(clock.Handles[0].Disposed);
    }

    [Fact]
    public async Task Unregister_failure_is_logged()
    {
        var log = new ListLogger();
        var clock = new ThrowingHandleClock();
        var s = new AudioSender(FakePcm.Frames(0), clock, null, Sink, 0, log, _time, () => _encoder);
        s.Stop();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Error is IOException);
    }

    [Fact]
    public async Task Stop_interrupts_a_real_wall_clock_wait()
    {
        // Real time: the sender blocks in its 20 ms pacing waits until stopped.
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s = new AudioSender(new EndlessPcm(), null, null, _ => first.TrySetResult(), 0, null, null, () => new FakeEncoder());
        s.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        s.Stop();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(s.FramesSent >= 1);
    }

    [Fact]
    public async Task Stop_interrupts_a_real_pause_wait()
    {
        var clock = new FakeMediaClock();
        clock.Pause.Close();
        var s = new AudioSender(new EndlessPcm(), clock, null, Sink, 0, null, null, () => new FakeEncoder());
        s.Start();
        s.Stop();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(_sent);
    }

    // ── pause gate ───────────────────────────────────────────────────────────

    [Fact]
    public void Holds_while_paused_then_resumes()
    {
        AudioSender? s = null;
        var clock = InStep(() => s);
        clock.Pause.Close();
        s = Sender(FakePcm.Frames(2), clock);
        var pauseWaits = 0;
        _time.OnWait = w =>
        {
            if (w == TimeSpan.FromMilliseconds(100) && ++pauseWaits == 3)
            {
                clock.Pause.Open();
            }
        };

        s.SendLoop();

        Assert.Equal(3, pauseWaits);
        Assert.Equal([0, 1], PayloadIds());
    }

    [Fact]
    public void Pause_mid_stream()
    {
        AudioSender? s = null;
        var clock = InStep(() => s);
        s = Sender(FakePcm.Frames(3), clock);
        _afterSend = () =>
        {
            if (_sent.Count == 1)
            {
                clock.Pause.Close();
            }
        };
        var pauseWaits = 0;
        _time.OnWait = w =>
        {
            if (w == TimeSpan.FromMilliseconds(100) && ++pauseWaits == 2)
            {
                clock.Pause.Open();
            }
        };

        s.SendLoop();

        Assert.Equal(2, pauseWaits);
        Assert.Equal(3, _sent.Count);
    }

    [Fact]
    public void Stop_while_paused()
    {
        var clock = new FakeMediaClock();
        clock.Pause.Close();
        var s = Sender(FakePcm.Frames(3), clock);
        _time.OnWait = _ => s.Stop();
        s.SendLoop();
        Assert.Empty(_sent);
        Assert.Equal(1, _time.WaitsOf(100));
    }

    [Fact]
    public void Restart_while_paused_drains_and_signals_player()
    {
        var clock = new FakeMediaClock();
        clock.Pause.Close();
        var pcm = FakePcm.Frames(1);
        var s = Sender(pcm, clock);
        var calls = 0;
        _time.OnWait = _ =>
        {
            if (++calls == 1)
            {
                clock.RestartSeq = 1; // seek while paused: FFmpeg restarted
                clock.SegmentOffset = 30.0;
            }
            else
            {
                s.Stop();
            }
        };

        s.SendLoop();

        Assert.Equal(1, clock.Handles[0].Signals);
        Assert.Equal(1, pcm.Drains);
        Assert.Equal((30.0, 1), (s.Follower.SegmentBase, s.Follower.SeenSeq));
    }

    [Fact]
    public void Pause_resets_drift_persistence()
    {
        var clock = new FakeMediaClock { SegmentFrames = 0 };
        clock.Pause.Close();
        var s = Sender(FakePcm.Frames(1), clock);
        s.Follower.NetSilence = AudioFollower.MaxNetSilence; // read straight away after the pause
        s.Follower.DriftSince = 1.0;
        s.Follower.Correcting = true;
        var calls = 0;
        _time.OnWait = _ =>
        {
            if (++calls > 1)
            {
                clock.Pause.Open();
            }
        };

        s.SendLoop();

        Assert.Null(s.Follower.DriftSince);
        Assert.False(s.Follower.Correcting);
        Assert.Equal([0], PayloadIds());
    }

    // ── A/V clock follower and stale-audio drain ─────────────────────────────

    [Fact]
    public void Audio_behind_video_drops_frames_to_catch_up()
    {
        var clock = new FakeMediaClock { PositionFn = () => 1.0 }; // video already 1 s in, flowing
        var stats = new FakeAudioStats();
        var s = Sender(FakePcm.Frames(6), clock, stats: stats);
        s.FollowerNow = Ticks(0.0, 1.1, 1.4, 1.7, 2.0, 2.3, 2.6, 2.9);

        s.SendLoop();

        // Tick 1 starts the persistence timer; from tick 2 one frame is skipped per correction interval.
        Assert.Equal([0, 2, 4], PayloadIds());
        Assert.Equal(3, s.Corrections.Dropped);
        Assert.Equal(3, stats.Skips);
    }

    [Fact]
    public void A_skip_that_hits_end_of_input_is_not_counted()
    {
        var clock = new FakeMediaClock { PositionFn = () => 1.0 };
        var s = Sender(FakePcm.Frames(5), clock);
        s.FollowerNow = Ticks(0.0, 1.1, 1.4, 1.7, 2.0);
        s.SendLoop();
        Assert.Equal([0, 2, 4], PayloadIds());
        Assert.Equal(2, s.Corrections.Dropped);
    }

    [Fact]
    public void Audio_in_step_with_video_is_untouched()
    {
        AudioSender? s = null;
        var clock = InStep(() => s);
        s = Sender(FakePcm.Frames(4), clock);
        s.SendLoop();
        Assert.Equal([0, 1, 2, 3], PayloadIds());
        Assert.Equal(new AudioCorrections(0, 0), s.Corrections);
    }

    [Fact]
    public void Audio_waits_silently_for_first_video_frame()
    {
        // Before the segment's first frame, audio waits (bounded) instead of running ahead of the picture.
        var clock = new FakeMediaClock { PositionFn = () => 5.0, SegmentFrames = 0 };
        var s = Sender(FakePcm.Frames(4), clock);
        s.SendLoop();
        Assert.Equal(AudioFollower.MaxNetSilence + 4, _sent.Count);
        Assert.Equal(4, s.Follower.SegmentFrames); // every real frame still played after the wait
        Assert.Equal(AudioFollower.MaxNetSilence, s.Follower.NetSilence);
        Assert.Equal(new AudioCorrections(0, 0), s.Corrections);
    }

    [Fact]
    public void Wait_for_first_frame_is_budgeted()
    {
        var clock = new FakeMediaClock { SegmentFrames = 0 };
        Sender(FakePcm.Frames(5), clock).SendLoop();
        Assert.Equal(AudioFollower.MaxNetSilence + 5, _sent.Count);
    }

    [Fact]
    public void Restart_rebases_audio_position_and_drains()
    {
        AudioSender? s = null;
        var clock = InStep(() => s);
        var pcm = FakePcm.Frames(4);
        pcm.DrainResult = 7;
        var log = new ListLogger();
        s = Sender(pcm, clock, log: log);
        _afterSend = () =>
        {
            if (_sent.Count == 1) // FFmpeg restarted at 120 s after the 1st frame
            {
                clock.RestartSeq = 1;
                clock.SegmentOffset = 120.0;
            }
        };

        s.SendLoop();

        Assert.Equal(1, pcm.Drains);
        Assert.Equal(120.0 + (3 * 0.02), s.AudioPosition, 9);
        Assert.Equal(1, s.Follower.SeenSeq);
        Assert.Equal(4, _sent.Count);
        Assert.Equal(1, clock.Handles[0].Signals);
        Assert.Contains(log.Entries, e => e.Message.Contains("source restarted at 120.0s") && e.Message.Contains("dropped 7 stale bytes"));
    }

    [Fact]
    public void Restart_resets_drift_tracking()
    {
        var clock = new FakeMediaClock();
        var s = Sender(FakePcm.Frames(0), clock);
        s.Follower.DriftSince = 1.0;
        s.Follower.Correcting = true;
        Assert.False(s.CheckRestart());
        clock.RestartSeq = 1;
        Assert.True(s.CheckRestart());
        Assert.Null(s.Follower.DriftSince);
        Assert.False(s.Follower.Correcting);
    }

    [Fact]
    public void Restart_resets_settle_and_episode_counts()
    {
        var clock = new FakeMediaClock();
        var s = Sender(FakePcm.Frames(0), clock);
        s.Follower.SettleSteps = 9;
        s.Follower.EpisodeSteps = 4;
        clock.RestartSeq = 1;
        Assert.True(s.CheckRestart());
        Assert.Equal((0, 0), (s.Follower.SettleSteps, s.Follower.EpisodeSteps));
    }

    [Fact]
    public void No_clock_never_restarts_or_follows()
    {
        var s = Sender(FakePcm.Frames(2));
        Assert.False(s.CheckRestart());
        s.SendLoop();
        Assert.Equal([0, 1], PayloadIds());
    }

    [Fact]
    public void Initial_restart_seq_and_offset_are_adopted()
    {
        var clock = new FakeMediaClock { RestartSeq = 4, SegmentOffset = 60.0 };
        var s = Sender(FakePcm.Frames(0), clock);
        Assert.False(s.CheckRestart());
        Assert.Equal(60.0, s.AudioPosition);
    }

    [Fact]
    public void Pending_seek_sends_silence_without_reading()
    {
        var clock = new FakeMediaClock { PositionFn = () => 100.0, SeekPending = true };
        var pcm = FakePcm.Frames(3);
        var s = Sender(pcm, clock);
        _afterSend = () =>
        {
            if (_sent.Count == 4)
            {
                s.Stop();
            }
        };

        s.SendLoop();

        Assert.Equal(0, pcm.Reads); // old segment's audio not played or "caught up"
        Assert.Equal(new AudioCorrections(0, 0), s.Corrections);
        Assert.All(_sent, p => Assert.Equal(0, p[^1]));
    }

    [Fact]
    public void Pending_seek_keeps_draining_so_ffmpeg_never_blocks()
    {
        var clock = new FakeMediaClock { SeekPending = true };
        var pcm = FakePcm.Frames(1);
        var s = Sender(pcm, clock);
        _afterSend = () =>
        {
            if (_sent.Count == 3)
            {
                s.Stop();
            }
        };

        s.SendLoop();

        Assert.Equal(3, pcm.Drains);
    }

    // ── persistent-drift follower (anti rubber-banding) ──────────────────────

    [Fact]
    public void Transient_lag_is_not_corrected()
    {
        // Video a few frames behind while still flowing: both sides self-correct to the wall clock.
        AudioSender? s = null;
        var clock = InStep(() => s, -0.1); // 100 ms behind
        s = Sender(FakePcm.Frames(10), clock);
        s.FollowerNow = Ticking(0.02);
        s.SendLoop();
        Assert.Equal(Enumerable.Range(0, 10), PayloadIds());
        Assert.Equal(new AudioCorrections(0, 0), s.Corrections);
    }

    [Fact]
    public void Large_drift_must_persist_before_correcting()
    {
        AudioSender? s = null;
        var clock = InStep(() => s, -0.5); // 500 ms ahead
        s = Sender(FakePcm.Frames(10), clock);
        s.FollowerNow = Ticking(0.02); // 10 ticks = 0.2 s < persistence window
        s.SendLoop();
        Assert.Equal(0, s.Corrections.Silence);
    }

    [Fact]
    public void Persistent_drift_corrected_gently_with_rate_limit()
    {
        AudioSender? s = null;
        var clock = InStep(() => s, -0.5);
        var stats = new FakeAudioStats();
        var times = new List<double>();
        stats.OnSilence = () => times.Add(s!.Follower.LastCorrection);
        s = Sender(FakePcm.Frames(30), clock, stats: stats);
        s.FollowerNow = Ticking(0.1); // 100 ms per tick

        s.SendLoop();

        Assert.NotEmpty(times);
        Assert.True(times[0] >= 1.0); // only after the persistence window
        Assert.All(times.Zip(times.Skip(1)), p => Assert.True(p.Second - p.First >= 0.25 - 1e-9));
        Assert.Equal(times.Count, s.Corrections.Silence);
    }

    // ── settling window after a segment's first frame ────────────────────────

    [Fact]
    public void Settling_window_aligns_quickly_and_is_capped()
    {
        // Right after start/seek the encoder delay leaves audio ahead: align immediately (no
        // persistence wait), but never more than the cap.
        var clock = new FakeMediaClock { PositionFn = () => 0.0, SegmentFrames = 1, Fps = 30.0 };
        var s = Sender(FakePcm.Frames(5), clock);
        _afterSend = () =>
        {
            if (_sent.Count == 25)
            {
                s.Stop();
            }
        };

        s.SendLoop();

        Assert.Equal(AudioFollower.MaxSettleSteps, s.Corrections.Silence);
        Assert.Equal(5, s.Follower.SegmentFrames); // all real audio still played afterwards
    }

    [Fact]
    public void Settling_window_skips_when_audio_behind()
    {
        var clock = new FakeMediaClock { PositionFn = () => 0.2, SegmentFrames = 1, Fps = 30.0 };
        var s = Sender(FakePcm.Frames(8), clock);
        s.SendLoop();
        Assert.True(s.Corrections.Dropped >= 1);
    }

    [Fact]
    public void Settling_ends_after_window()
    {
        var clock = new FakeMediaClock { PositionFn = () => 0.0, SegmentFrames = 30, Fps = 30.0 }; // 1 s at 30 fps
        var s = Sender(FakePcm.Frames(5), clock);
        s.FollowerNow = Ticking(0.02);
        s.SendLoop();
        Assert.Equal(new AudioCorrections(0, 0), s.Corrections);
    }

    [Fact]
    public void Default_follower_time_comes_from_the_time_provider()
    {
        var s = Sender(FakePcm.Frames(0));
        Assert.Equal(100.0, s.FollowerNow());
    }

    // ── PCM source ───────────────────────────────────────────────────────────

    [Fact]
    public void Drain_stale_empties_pipe_without_blocking()
    {
        using var writer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var reader = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        using var pcm = new StreamPcmSource(reader, reader.SafePipeHandle, ownsStream: false);

        writer.Write(new byte[5000]);
        Assert.Equal(5000, pcm.DrainAvailable());
        Assert.Equal(0, pcm.DrainAvailable()); // empty, writer still open: no block
        writer.Write(new byte[10]);
        writer.Dispose();
        Assert.Equal(10, pcm.DrainAvailable()); // then EOF
        Assert.Equal(0, pcm.DrainAvailable());
    }

    [Fact]
    public void Drain_stale_ignores_non_fd_sources()
    {
        using var pcm = new StreamPcmSource(new MemoryStream("abc"u8.ToArray()));
        Assert.Equal(0, pcm.DrainAvailable());
        var frame = new byte[Frame];
        Assert.Equal(3, pcm.ReadFrame(frame)); // short read = writer closed
        Assert.Equal(0, pcm.ReadFrame(frame));
    }

    [Fact]
    public void Drain_of_a_closed_descriptor_or_regular_file_is_zero()
    {
        var path = Path.GetTempFileName();
        try
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0);
            using var closed = new StreamPcmSource(fs, fs.SafeFileHandle);
            fs.Dispose();
            Assert.Equal(0, closed.DrainAvailable());

            using var empty = StreamPcmSource.OpenFifo(path); // a regular file works too: nothing buffered
            Assert.Equal(0, empty.DrainAvailable());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ioctl_failure_reports_minus_one()
    {
        // A descriptor number nothing has open: the kernel rejects it (EBADF).
        using var bogus = new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(int.MaxValue), ownsHandle: false);
        Assert.Equal(-1, PosixIo.BytesAvailable(bogus));
        Assert.Equal(0, new StreamPcmSource(new MemoryStream(), bogus).DrainAvailable());
    }

    [Fact]
    public async Task Fifo_reads_whole_frames_and_drains_without_blocking()
    {
        var dir = Directory.CreateTempSubdirectory("slopsoil-audio-");
        var path = Path.Combine(dir.FullName, "audio.pcm");
        try
        {
            using (var mk = Process.Start("mkfifo", path))
            {
                await mk.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, mk.ExitCode);
            }

            var open = Task.Run(() => StreamPcmSource.OpenFifo(path), TestContext.Current.CancellationToken);
            await using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 0))
            {
                using var pcm = await open.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                writer.Write(new byte[(2 * Frame) + 5]);
                var frame = new byte[Frame];
                Assert.Equal(Frame, pcm.ReadFrame(frame));
                Assert.Equal(Frame + 5, pcm.DrainAvailable());
                Assert.Equal(0, pcm.DrainAvailable());

                writer.Write(new byte[100]);
                await writer.DisposeAsync();
                Assert.Equal(100, pcm.ReadFrame(frame)); // short read: writer closed
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class EndlessPcm : IPcmSource
    {
        public int ReadFrame(Span<byte> frame) => frame.Length;

        public int DrainAvailable() => 0;
    }

    private sealed class ThrowingHandleClock : IMediaClock
    {
        public double Fps => 0;

        public double Position => 0;

        public int RestartSeq => 0;

        public double SegmentOffset => 0;

        public int SegmentFrames => 1;

        public bool SeekPending => false;

        public bool IsSourceActive => false;

        public PauseGate Pause { get; } = new();

        public IAudioFollowerHandle RegisterAudioFollower() => new Handle();

        private sealed class Handle : IAudioFollowerHandle
        {
            public void SignalDrained() => throw new IOException("player gone");

            public void Dispose()
            {
            }
        }
    }
}

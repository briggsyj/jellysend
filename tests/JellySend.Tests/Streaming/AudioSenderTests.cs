using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using JellySend.Discord.Rtp;
using JellySend.Streaming;
using JellySend.Streaming.Audio;
using JellySend.Streaming.Contracts;

namespace JellySend.Tests.Streaming;

/// <summary>Port of the <c>GoLiveAudioSender</c> cases in <c>tests/test_golive.py</c> (T027, T033, T048).</summary>
public sealed class AudioSenderTests
{
    private const int Frame = FakePcm.Frame;
    private const int Overhead = RtpHeader.Size + 16 + 4; // header + AEAD tag + nonce suffix

    private readonly StepTimeProvider _time = new();
    private readonly FakeEncoder _encoder = new();
    private readonly List<byte[]> _sent = [];
    private readonly List<long> _sentAt = [];
    private Action? _afterSend;

    private AudioSender Sender(IPcmSource pcm, FakeMediaClock? clock = null, int avSyncMs = 0, IAudioStats? stats = null, ILogger? log = null, double buffer = 0) =>
        new(pcm, clock, stats, Sink, avSyncMs, log, _time, () => _encoder, buffer);

    private void Sink(ReadOnlySpan<byte> opus)
    {
        _sent.Add(opus.ToArray());
        _sentAt.Add(_time.GetTimestamp());
        _afterSend?.Invoke();
    }

    private List<int> PayloadIds() => _sent.Select(p => (int)p[^1]).ToList();

    /// <summary>Send times in ms relative to <paramref name="origin"/>.</summary>
    private List<double> SentMs(long origin) => _sentAt.Select(t => TimeSpan.FromTicks(t - origin).TotalMilliseconds).ToList();

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>A frame whose bytes (and so its fake Opus payload id) are all <paramref name="id"/>.</summary>
    private static byte[] Pcm(int id) => Enumerable.Repeat((byte)id, Frame).ToArray();

    /// <summary>
    /// A playing clock (25 fps) whose segment-0 origin, 100 ms after the moment the read-ahead is
    /// <paramref name="ready"/> (waited for in real time), appears from then on.
    /// </summary>
    private FakeMediaClock Clock(Func<bool> ready, out Func<long> origin)
    {
        PlayoutAnchor? anchor = null;
        origin = () => anchor!.Origin;
        return new FakeMediaClock
        {
            Fps = 25,
            SegmentFrames = 1000,
            SourceActive = () => true,
            AnchorFn = () =>
            {
                if (anchor is null)
                {
                    SpinUntil(ready);
                    anchor = new PlayoutAnchor(0, _time.GetTimestamp() + Ms(100).Ticks);
                }

                return anchor;
            },
        };
    }

    private static void SpinUntil(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), "timed out waiting on the read-ahead");

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

    // ── STREAM_AV_SYNC_MS (own origin) ───────────────────────────────────────

    [Fact]
    public void Positive_av_sync_discards_backlog()
    {
        var s = Sender(FakePcm.Frames(5), avSyncMs: 40); // 2 frames' slots are already gone at the origin
        s.SendLoop();
        Assert.Equal([2, 3, 4], PayloadIds());
        Assert.Equal(new AudioCorrections(0, 0), s.Corrections); // by design, not a correction
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
    }

    [Fact]
    public void Negative_av_sync_delays_start()
    {
        Sender(FakePcm.Frames(1), avSyncMs: -250).SendLoop();
        Assert.Equal(Ms(250), _time.Waits[0]);
        Assert.Single(_sent);
    }

    [Fact]
    public void Av_sync_is_clamped_to_five_seconds()
    {
        Sender(FakePcm.Frames(1), avSyncMs: -9000).SendLoop();
        Assert.Equal(TimeSpan.FromSeconds(5), _time.Waits[0]);
    }

    // ── read-ahead, end of input, stop, pacing ───────────────────────────────

    [Fact]
    public void Short_read_without_a_clock_ends_the_stream()
    {
        Sender(new FakePcm([new byte[100]])).SendLoop();
        Assert.Empty(_sent);
    }

    [Fact]
    public void Read_ahead_is_bounded_and_back_pressures_the_fifo()
    {
        var pcm = new EndlessPcm();
        var clock = new FakeMediaClock { SourceActive = () => true }; // never anchored: nothing plays
        var s = Sender(pcm, clock, buffer: 1.0);
        Assert.Equal(100, s.BufferCapacity); // (1 s + 1 s headroom) / 20 ms
        Assert.Equal(50, Sender(pcm, buffer: 0).BufferCapacity);
        _time.OnWait = _ =>
        {
            if (s.ReadAheadSettled)
            {
                Assert.Equal(100, s.BufferedFrames);
                s.Stop();
            }
        };

        s.SendLoop();

        Assert.Empty(_sent);
        Assert.Equal(101, pcm.Reads); // the full buffer plus the frame in hand: FFmpeg is held back by the FIFO
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
            _time.CreateTimer(_ => { }, null, Ms(30), Timeout.InfiniteTimeSpan); // run late
            s.Stop();
        };
        s.SendLoop();
        Assert.Single(_sent);
    }

    [Fact]
    public void Paces_to_an_absolute_schedule()
    {
        var s = Sender(FakePcm.Frames(3));
        s.SendLoop();
        Assert.Equal([20, 20], _time.Waits.Select(w => (int)w.TotalMilliseconds));
        Assert.Equal([0, 20, 40], SentMs(_sentAt[0]));
    }

    [Fact]
    public void A_briefly_stalled_sender_catches_up_in_a_burst()
    {
        var s = Sender(FakePcm.Frames(5));
        _afterSend = () =>
        {
            if (_sent.Count == 1)
            {
                _time.CreateTimer(_ => { }, null, Ms(70), Timeout.InfiniteTimeSpan); // the thread stalls 70 ms
            }
        };
        s.SendLoop();

        Assert.Equal([0, 1, 2, 3, 4], PayloadIds());
        Assert.Equal([0, 70, 70, 70, 80], SentMs(_sentAt[0])); // slots 1-3 go late, slot 4 on time again
        Assert.Equal(new AudioCorrections(0, 0), s.Corrections);
    }

    [Fact]
    public void A_long_stalled_sender_skips_the_slots_it_missed()
    {
        var s = Sender(FakePcm.Frames(16));
        _afterSend = () =>
        {
            if (_sent.Count == 1)
            {
                _time.CreateTimer(_ => { }, null, Ms(250), Timeout.InfiniteTimeSpan);
            }
        };
        s.SendLoop();

        // Slots 1 and 2 are more than 200 ms late: dropped. Slots 3-12 go late in a burst, 13-15 on time.
        Assert.Equal([0, .. Enumerable.Range(3, 13)], PayloadIds());
        Assert.Equal([0, .. Enumerable.Repeat(250.0, 10), 260, 280, 300], SentMs(_sentAt[0]));
        Assert.Equal(new AudioCorrections(0, 2), s.Corrections);
    }

    // ── one clock (spec 004) ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(-60)]
    public void Audio_frame_k_goes_at_origin_plus_k_frames_minus_av_sync(int avSyncMs)
    {
        AudioSender? s = null;
        var clock = Clock(() => s!.ReadAheadSettled, out var origin);
        clock.SourceActive = () => false; // one segment: the reader ends at its end
        s = Sender(FakePcm.Frames(3), clock, avSyncMs);

        s.SendLoop();

        Assert.Equal([0, 1, 2], PayloadIds());
        Assert.Equal([0 - avSyncMs, 20 - avSyncMs, 40 - avSyncMs], SentMs(origin()));
        Assert.Equal(new AudioCorrections(0, 0), s.Corrections);
    }

    [Fact]
    public void Nothing_is_sent_before_the_segment_is_anchored()
    {
        PlayoutAnchor? anchor = null;
        AudioSender? s = null;
        var clock = new FakeMediaClock { Fps = 25, SegmentFrames = 1000, SourceActive = () => anchor is null, AnchorFn = () => anchor };
        s = Sender(FakePcm.Frames(2), clock);
        var polls = 0;
        _time.OnWait = w =>
        {
            if (w == Ms(2) && ++polls == 5)
            {
                SpinUntil(() => s.ReadAheadSettled);
                Assert.Empty(_sent); // pre-roll: buffered, not played
                Assert.Equal(2, s.BufferedFrames);
                anchor = new PlayoutAnchor(0, _time.GetTimestamp() + Ms(2).Ticks);
            }
        };

        s.SendLoop();

        Assert.Equal([0, 1], PayloadIds());
        Assert.Equal([0, 20], SentMs(anchor!.Origin));
    }

    [Fact]
    public void Underrun_sends_silence_on_schedule_and_drops_the_late_frames()
    {
        var gate = new GatedPcm();
        gate.Release(Pcm(10), Pcm(11));
        AudioSender? s = null;
        var clock = Clock(() => s!.BufferedFrames >= 2, out var origin);
        var stats = new FakeAudioStats();
        s = Sender(gate, clock, stats: stats);
        _afterSend = () =>
        {
            if (_sent.Count == 4)
            {
                // Frames 12 and 13 arrive after their slots (2, 3) were filled with silence.
                gate.Release(Pcm(12), Pcm(13), Pcm(14));
                SpinUntil(() => s.BufferedFrames == 3);
            }
            else if (_sent.Count == 5)
            {
                s.Stop();
            }
        };

        s.SendLoop();

        Assert.Equal([10, 11, 0, 0, 14], PayloadIds());
        Assert.Equal([0, 20, 40, 60, 80], SentMs(origin())); // the schedule never moved
        Assert.Equal(new AudioCorrections(2, 2), s.Corrections);
        Assert.Equal((2, 2), (stats.Silence, stats.Skips));
    }

    [Fact]
    public void Two_second_read_stall_with_a_five_second_buffer_is_inaudible()
    {
        // SC-003: the source stops delivering for 2 s while 5 s are buffered.
        var gate = new GatedPcm();
        gate.Release([.. Enumerable.Range(0, 250).Select(i => Pcm(i % 200))]);
        AudioSender? s = null;
        var clock = Clock(() => s!.BufferedFrames >= 250, out var origin);
        var stats = new FakeAudioStats();
        s = Sender(gate, clock, stats: stats, buffer: 5);
        _afterSend = () =>
        {
            if (_sent.Count == 100)
            {
                gate.Release([.. Enumerable.Range(250, 150).Select(i => Pcm(i % 200))]); // 2 s later
                SpinUntil(() => s.BufferedFrames == 300);
            }
            else if (_sent.Count == 400)
            {
                s.Stop();
            }
        };

        s.SendLoop();

        Assert.Equal(400, _sent.Count);
        Assert.Equal(new AudioCorrections(0, 0), s.Corrections);
        Assert.Equal(0, stats.Silence);
        Assert.Equal(7980, SentMs(origin())[^1]);
        Assert.True(stats.Buffered.Max() >= TimeSpan.FromSeconds(4.98));
    }

    [Fact]
    public void Pause_holds_audio_and_follows_the_shifted_origin()
    {
        AudioSender? s = null;
        var anchor = new PlayoutAnchor(0, _time.GetTimestamp() + Ms(100).Ticks);
        var clock = new FakeMediaClock { Fps = 25, SegmentFrames = 1000, SourceActive = () => false };
        clock.AnchorFn = () =>
        {
            SpinUntil(() => s!.ReadAheadSettled);
            return clock.Anchor;
        };
        clock.Anchor = anchor;
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
            if (w == Ms(100) && ++pauseWaits == 3)
            {
                // The player moves the origin by the pause before it opens the gate.
                clock.Anchor = anchor with { Origin = anchor.Origin + Ms(300).Ticks };
                clock.Pause.Open();
            }
        };

        s.SendLoop();

        Assert.Equal([0, 1, 2], PayloadIds());
        Assert.Equal([0, 320, 340], SentMs(anchor.Origin));
        Assert.Equal(3, pauseWaits);
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
    public void Restart_flushes_the_old_segment_and_re_anchors_on_the_new_one()
    {
        AudioSender? s = null;
        var clock = new FakeMediaClock { Fps = 25, SegmentFrames = 1000, SourceActive = () => true };
        var first = new PlayoutAnchor(0, _time.GetTimestamp() + Ms(100).Ticks); // nothing polls before the read-ahead settled
        PlayoutAnchor? second = null;
        var gate = new GatedPcm();
        gate.Release(Pcm(1), Pcm(2), Pcm(3), Pcm(4), Pcm(5));
        gate.EndOfWriter(); // the old FFmpeg goes away
        var released = false;
        var refilled = false;
        clock.AnchorFn = () =>
        {
            if (clock.RestartSeq == 0)
            {
                SpinUntil(() => s!.ReadAheadSettled);
                return first;
            }

            if (!released)
            {
                return null;
            }

            if (!refilled)
            {
                SpinUntil(() => s!.BufferedFrames >= 2);
                refilled = true;
            }

            return second;
        };
        var log = new ListLogger();
        s = Sender(gate, clock, log: log);
        _afterSend = () =>
        {
            if (_sent.Count == 2)
            {
                // Seek: FFmpeg restarted at 30 s.
                clock.RestartSeq = 1;
                clock.SegmentOffset = 30.0;
                second = new PlayoutAnchor(1, _time.GetTimestamp() + Ms(500).Ticks);
            }
            else if (_sent.Count == 4)
            {
                s.Stop();
            }
        };
        _time.OnWait = _ =>
        {
            if (!released && clock.Handles[0].Signals == 1)
            {
                // The player starts the new FFmpeg only after the old audio was drained.
                released = true;
                gate.Release(Pcm(21), Pcm(22));
            }
        };

        s.SendLoop();

        Assert.Equal([1, 2, 21, 22], PayloadIds());
        Assert.Equal([0, 20], SentMs(first.Origin).Take(2));
        Assert.Equal([0, 20], SentMs(second!.Origin).Skip(2));
        Assert.True(clock.Handles[0].Signals >= 1);
        Assert.Contains(log.Entries, e => e.Message.StartsWith("GoLiveAudio: source restarted at 30.0s — dropped 3 buffered frames", StringComparison.Ordinal));
    }

    [Fact]
    public void Restart_while_paused_drains_and_signals_player()
    {
        var clock = new FakeMediaClock();
        clock.Pause.Close();
        var s = Sender(FakePcm.Frames(1), clock);
        var calls = 0;
        _time.OnWait = _ =>
        {
            if (++calls == 1)
            {
                clock.RestartSeq = 1; // seek while paused: FFmpeg restarted
                clock.SegmentOffset = 30.0;
            }
            else if (clock.Handles[0].Signals > 0)
            {
                s.Stop();
            }
        };

        s.SendLoop();

        Assert.Equal(1, clock.Handles[0].Signals);
        Assert.Empty(_sent);
    }

    [Fact]
    public void Pending_seek_sends_nothing_and_discards_the_stale_audio()
    {
        var pcm = new EndlessPcm();
        var clock = new FakeMediaClock { SeekPending = true, SourceActive = () => true, Anchor = new PlayoutAnchor(0, 0) };
        var s = Sender(pcm, clock);
        _time.OnWait = _ =>
        {
            if (pcm.Reads > 500)
            {
                s.Stop(); // still reading: FFmpeg never blocks on the FIFO
            }
        };

        s.SendLoop();

        Assert.Empty(_sent);
        Assert.Equal(0, s.BufferedFrames);
    }

    [Fact]
    public void End_of_a_segment_sends_no_silence_and_ends_with_the_source()
    {
        var active = true;
        AudioSender? s = null;
        var clock = Clock(() => s!.ReadAheadSettled, out _);
        clock.SourceActive = () => active;
        s = Sender(FakePcm.Frames(2), clock);
        _time.OnWait = w =>
        {
            if (_sent.Count == 2 && w == Ms(2))
            {
                active = false; // the player ended
            }
        };

        s.SendLoop();

        Assert.Equal([0, 1], PayloadIds());
        Assert.Equal(new AudioCorrections(0, 0), s.Corrections);
    }

    [Fact]
    public void Drift_and_buffer_fill_are_reported_on_the_shared_clock()
    {
        AudioSender? s = null;
        var clock = Clock(() => s!.ReadAheadSettled, out _);
        clock.SourceActive = () => false;
        clock.SegmentFrames = 0; // video stuck at its first frame: it falls behind by 20 ms per audio frame
        var stats = new FakeAudioStats();
        s = Sender(FakePcm.Frames(3), clock, stats: stats);

        s.SendLoop();

        Assert.Equal([0, 20, 40], stats.Drifts.Select(d => Math.Round(d)));
        Assert.Equal([Ms(40), Ms(20), Ms(0), Ms(0)], stats.Buffered);
    }

    [Fact]
    public void Initial_restart_seq_is_adopted()
    {
        AudioSender? s = null;
        var anchor = new PlayoutAnchor(7, _time.GetTimestamp());
        var clock = new FakeMediaClock { RestartSeq = 7, SourceActive = () => false };
        clock.AnchorFn = () =>
        {
            SpinUntil(() => s!.ReadAheadSettled);
            return anchor;
        };
        s = Sender(FakePcm.Frames(1), clock);
        s.SendLoop();
        Assert.Single(_sent);
        Assert.Equal(0, clock.Handles[0].Signals); // no restart drain
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
    public async Task Sender_exit_unregisters_from_the_player()
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

    [Fact]
    public void Reader_errors_are_logged_and_end_the_stream()
    {
        var log = new ListLogger();
        Sender(new ThrowingPcm(), log: log).SendLoop();
        Assert.Empty(_sent);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message == "GoLiveAudio: PCM reader failed");
    }

    [Fact]
    public void A_closed_fifo_ends_the_reader_quietly()
    {
        var log = new ListLogger();
        var s = Sender(new ThrowingPcm(new ObjectDisposedException("fifo")), log: log);
        s.SendLoop();
        Assert.Empty(_sent);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
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
        var dir = Directory.CreateTempSubdirectory("jellysend-audio-");
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
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public int ReadFrame(Span<byte> frame)
        {
            Interlocked.Increment(ref _reads);
            return frame.Length;
        }

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

        public PlayoutAnchor? Anchor => null;

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

    /// <summary>PCM whose frames appear when the test releases them; blocks in between (a stalled FFmpeg).</summary>
    private sealed class GatedPcm : IPcmSource
    {
        private readonly BlockingCollection<byte[]> _chunks = [];

        public void Release(params byte[][] frames)
        {
            foreach (var f in frames)
            {
                _chunks.Add(f);
            }
        }

        /// <summary>The writer closes its end (a short read).</summary>
        public void EndOfWriter() => _chunks.Add([]);

        public int ReadFrame(Span<byte> frame)
        {
            if (!_chunks.TryTake(out var chunk, TimeSpan.FromSeconds(20)))
            {
                return 0;
            }

            chunk.CopyTo(frame);
            return chunk.Length;
        }

        public int DrainAvailable() => 0;
    }

    private sealed class ThrowingPcm(Exception? error = null) : IPcmSource
    {
        public int ReadFrame(Span<byte> frame) => throw error ?? new InvalidOperationException("boom");

        public int DrainAvailable() => 0;
    }
}

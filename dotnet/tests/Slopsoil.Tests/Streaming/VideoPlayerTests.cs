using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Slopsoil.Discord.Rtp;
using Slopsoil.Streaming.Diagnostics;
using Slopsoil.Streaming.Video;

namespace Slopsoil.Tests.Streaming;

public sealed class VideoPlayerTests : IDisposable
{
    private static readonly EncoderConfig Hw = new("h264_vaapi", ["-vaapi_device", "/dev/x"], ["-b:v", "1k"], "hwupload");
    private static readonly EncoderConfig Sw = new("libopenh264", [], ["-b:v", "1k"], "scale=1:1");

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("slopsoil-vp");
    private readonly FakeProcessRunner _runner = new();
    private readonly FakeFifoFactory _fifos = new();
    private readonly RecordingFrameSink _sink = new();
    private readonly SteppingTimeProvider _time = new();
    private readonly VideoTestLogger _log = new();
    private VideoPlayer? _player;

    public void Dispose()
    {
        _player?.Stop();
        _player?.Completion.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _dir.Delete(recursive: true);
    }

    private VideoPlayer Player(Func<VideoPlayerOptions, VideoPlayerOptions>? configure = null, StreamDiagnostics? diagnostics = null)
    {
        var options = new VideoPlayerOptions
        {
            Url = "http://user:pw@host:8080/live.ts",
            Encoder = Sw,
            VideoSsrc = 1001,
            Fps = 25.0,
            TempDirectory = _dir.FullName,
            Resolution = "1280:720",
            Bitrate = "1k",
        };
        _player = new VideoPlayer(configure?.Invoke(options) ?? options, _sink, _runner, _fifos, diagnostics, _time, _log);
        return _player;
    }

    private FakeMediaProcess Proc(byte[] stdout, bool running = false, int step = 65_536, bool blockAtEnd = false, string stderr = "")
    {
        var p = new FakeMediaProcess(stdout, stderr, running, step: step, blockAtEnd: blockAtEnd);
        _runner.Add(p);
        return p;
    }

    private static async Task<VideoPlayerEnd> Ended(VideoPlayer player) => await Waits.Within(player.Completion);

    private static string Opt(IReadOnlyList<string> cmd, string flag) => cmd[cmd.ToList().IndexOf(flag) + 1];

    private void AssertFrames(params byte[][][] expected) =>
        Assert.Equal(expected.Select(Hex), _sink.Frames.Select(f => Hex([.. f])));

    private static string Hex(byte[][] frame) => string.Join('|', frame.Select(Convert.ToHexString));

    // ── frame grouping / natural end ─────────────────────────────────────

    [Theory]
    [InlineData(65_536)]
    [InlineData(3)]
    public async Task Groups_nals_into_frames_and_ends_naturally(int step)
    {
        Proc([.. Nals.AnnexB(Nals.Aud, Nals.Sps, Nals.Pps, Nals.Sei, Nals.Idr, Nals.Filler, Nals.Aud, Nals.P1, Nals.Aud, Nals.P2, Nals.Aud), .. Nals.StartCode], step: step);
        var player = Player();
        Assert.False(player.IsSourceActive);
        player.Start();

        Assert.Equal(VideoPlayerEnd.Natural, await Ended(player));
        AssertFrames([SpsRewriter.Rewrite(Nals.Sps), Nals.Pps, Nals.Sei, Nals.Idr], [Nals.P1], [Nals.P2]);
        Assert.All(_sink.TimestampIncrements, i => Assert.Equal(3600, i));
        Assert.Equal(3, player.SegmentFrames);
        Assert.True(player.FirstFrameSent.IsCompletedSuccessfully);
        Assert.False(player.IsSourceActive);
        Assert.False(player.StopRequested);
        var cmd = Assert.Single(_runner.Started);
        Assert.Equal("ffmpeg", cmd[0]);
        Assert.Equal(player.AudioFifoPath, cmd[^1]);
        Assert.Equal(Path.Combine(_dir.FullName, "slopsoil_1001_audio.fifo"), player.AudioFifoPath);
        Assert.Equal(["create " + player.AudioFifoPath, "remove " + player.AudioFifoPath], _fifos.Calls);
        Assert.Contains("H264VideoPlayer stopped", _log.Text);
    }

    [Fact]
    public async Task Slice_without_aud_starts_a_new_frame()
    {
        Proc(Nals.AnnexB(Nals.Idr, Nals.P1, Nals.P2));
        var player = Player();
        player.Start();
        await Ended(player);
        // The tail goes through the splitter too (Python appended it to the current frame).
        AssertFrames([Nals.Idr], [Nals.P1], [Nals.P2]);
    }

    [Fact]
    public async Task Empty_output_emits_nothing()
    {
        Proc([]);
        var player = Player();
        player.Start();
        Assert.Equal(VideoPlayerEnd.Natural, await Ended(player));
        Assert.Empty(_sink.Frames);
        Assert.Equal(0, player.SegmentFrames);
        Assert.False(player.FirstFrameSent.IsCompleted);
    }

    [Fact]
    public async Task Position_is_offset_plus_frames_over_fps()
    {
        Proc(Nals.Frames(50));
        var player = Player(o => o with { StartOffset = 60.0 });
        Assert.Equal(60.0, player.Position);
        Assert.Equal(60.0, player.SegmentOffset);
        player.Start();
        await Ended(player);
        Assert.Equal(62.0, player.Position, 6);
        Assert.Equal("60.000", Opt(_runner.Started.Single(), "-ss"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void No_seek_at_the_start(double offset)
    {
        var player = Player(o => o with { StartOffset = offset });
        Assert.Equal(0.0, player.Position);
    }

    [Fact]
    public async Task Command_carries_the_player_settings_and_is_logged_redacted()
    {
        Proc([]);
        var player = Player(o => o with { Url = "http://user:secret@host:8080/live.ts?api_key=k3y", HasAudio = false, Aspect = 2.0, Live = false, ProbeSize = 77 });
        player.Start();
        await Ended(player);

        var cmd = _runner.Started.Single();
        Assert.Equal("http://user:secret@host:8080/live.ts?api_key=k3y", Opt(cmd, "-i"));
        Assert.Contains("anullsrc=r=48000:cl=stereo", cmd);
        Assert.Equal("77", Opt(cmd, "-probesize"));
        Assert.Equal("25", Opt(cmd, "-r"));
        Assert.False(player.HasAudio);
        Assert.Equal(2.0, player.Aspect);
        Assert.DoesNotContain("secret", _log.Text);
        Assert.DoesNotContain("k3y", _log.Text);
        Assert.Contains("http://host:8080/live.ts", _log.Text);
        Assert.Contains("FFmpeg encoder=libopenh264 command: ffmpeg -y", _log.Text);
    }

    [Fact]
    public async Task Segment_hook_runs_before_each_start_and_failures_are_tolerated()
    {
        Proc([.. Nals.Frames(1)]);
        var calls = 0;
        var player = Player(o => o with { OnSegmentStarting = () => { calls++; throw new InvalidOperationException("dave"); } });
        player.Start();
        Assert.Equal(VideoPlayerEnd.Natural, await Ended(player));
        Assert.Equal(1, calls);
        Assert.Single(_sink.Frames);
        Assert.Contains("segment start hook failed: dave", _log.Text);
    }

    [Fact]
    public async Task Start_failure_ends_as_failed_and_cleans_up()
    {
        _runner.StartError = new Win32Exception(2, "No such file or directory");
        var player = Player();
        player.Start();
        Assert.Equal(VideoPlayerEnd.Failed, await Ended(player));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.StartsWith("H264VideoPlayer error", StringComparison.Ordinal));
        Assert.Equal("remove " + player.AudioFifoPath, _fifos.Calls.Last());
        Assert.False(player.IsSourceActive);
    }

    [Fact]
    public async Task Start_twice_throws_and_start_after_stop_does_nothing()
    {
        Proc([]);
        var player = Player();
        player.Start();
        Assert.Throws<InvalidOperationException>(player.Start);
        await Ended(player);

        var stopped = Player();
        stopped.Stop();
        stopped.Start();
        Assert.Equal(VideoPlayerEnd.Stopped, await Ended(stopped));
        Assert.Single(_runner.Started);
    }

    // ── stop ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_requested_while_ffmpeg_starts_kills_it()
    {
        var proc = Proc(Nals.AnnexB(Nals.Idr, Nals.Aud), running: true);
        var player = Player();
        _runner.OnStart = _ => player.Stop();
        player.Start();

        Assert.Equal(VideoPlayerEnd.Stopped, await Ended(player));
        Assert.Equal(1, proc.Terminated);
        Assert.Empty(_sink.Frames);
        Assert.True(player.StopRequested);
    }

    [Fact]
    public async Task Stop_mid_stream_sends_nothing_more_signals_first_then_the_player_closes_stdout()
    {
        var proc = Proc(Nals.AnnexB(Nals.Idr, Nals.Aud, Nals.P1, Nals.Aud, Nals.P2, Nals.Aud), running: true);
        var player = Player();
        _sink.OnSend = _ => player.Stop();
        player.Start();

        Assert.Equal(VideoPlayerEnd.Stopped, await Ended(player));
        AssertFrames([Nals.Idr]);
        // Stop() only signals (closing stdout there could block behind the player thread's Read); the player thread
        // then closes stdout itself while killing the process.
        Assert.Equal("term", proc.Events.First());
        Assert.True(proc.StdoutClosed);
        Assert.True(proc.Disposed);
        var events = proc.Events.Count;
        player.Stop(); // idempotent
        Assert.Equal(events, proc.Events.Count);
    }

    [Fact]
    public async Task Stop_on_a_slice_boundary_does_not_flush_the_tail()
    {
        var proc = Proc(Nals.AnnexB(Nals.Idr, Nals.P1, Nals.P2), running: true);
        var player = Player();
        _sink.OnSend = _ => player.Stop();
        player.Start();

        await Ended(player);
        AssertFrames([Nals.Idr]);
        Assert.True(proc.Terminated >= 1);
    }

    [Fact]
    public async Task Stop_unblocks_a_waiting_read()
    {
        var proc = Proc(Nals.Frames(2), blockAtEnd: true);
        var player = Player();
        player.Start();
        await Waits.UntilAsync(() => _sink.Count == 2, "two frames");
        Assert.True(player.IsSourceActive);

        player.Stop();
        Assert.Equal(VideoPlayerEnd.Stopped, await Ended(player));
        Assert.True(proc.StdoutClosed);
        Assert.False(player.IsSourceActive);
    }

    [Fact]
    public async Task Ffmpeg_that_will_not_exit_is_killed()
    {
        var proc = Proc(Nals.AnnexB(Nals.Idr));
        proc.WaitTimesOut = true;
        var player = Player();
        player.Start();
        await Ended(player);
        Assert.Equal(1, proc.Killed);
        Assert.Contains("FFmpeg still running after stream end — killing", _log.Text);
    }

    [Fact]
    public void Kill_process_paths()
    {
        var player = Player();
        var exited = new FakeMediaProcess();
        player.KillProcess(exited);
        Assert.Empty(exited.Events);

        var graceful = new FakeMediaProcess(running: true);
        player.KillProcess(graceful);
        Assert.Equal(["close", "term"], graceful.Events);

        var stubborn = new FakeMediaProcess(running: true, waitTimesOut: true);
        player.KillProcess(stubborn);
        Assert.Equal(["close", "term", "kill"], stubborn.Events);
        Assert.Contains("FFmpeg did not exit after SIGTERM — sending SIGKILL", _log.Text);
    }

    // ── software fallback ────────────────────────────────────────────────

    [Fact]
    public async Task Falls_back_to_software_when_hardware_emits_nothing()
    {
        var hw = Proc([]);
        Proc(Nals.Frames(2));
        var player = Player(o => o with { Encoder = Hw, Fallback = Sw });
        player.Start();

        Assert.Equal(VideoPlayerEnd.Natural, await Ended(player));
        Assert.Equal(["h264_vaapi", "libopenh264"], _runner.Started.Select(c => Opt(c, "-c:v")));
        Assert.Same(Sw, player.Encoder);
        Assert.Equal(1, player.RestartSeq);
        Assert.Equal(2, _sink.Count);
        Assert.True(hw.Disposed);
        Assert.Contains("video encoder h264_vaapi produced no output — retrying with software encoder libopenh264", _log.Text);
        Assert.Equal("libopenh264", player.Diagnostics.Snapshot().Encoder);
    }

    [Fact]
    public async Task No_fallback_when_frames_were_emitted()
    {
        Proc(Nals.Frames(1));
        var player = Player(o => o with { Encoder = Hw, Fallback = Sw });
        player.Start();
        await Ended(player);
        Assert.Single(_runner.Started);
        Assert.Same(Hw, player.Encoder);
    }

    [Fact]
    public async Task No_fallback_when_stopped()
    {
        Proc([], running: true);
        var player = Player(o => o with { Encoder = Hw, Fallback = Sw });
        _runner.OnStart = _ => player.Stop();
        player.Start();
        Assert.Equal(VideoPlayerEnd.Stopped, await Ended(player));
        Assert.Single(_runner.Started);
    }

    [Fact]
    public async Task No_fallback_when_already_software()
    {
        Proc([]);
        var player = Player(o => o with { Encoder = Sw, Fallback = Sw });
        player.Start();
        await Ended(player);
        Assert.Single(_runner.Started);
    }

    // ── pacing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Frames_are_paced_to_absolute_deadlines()
    {
        Proc(Nals.Frames(4));
        var player = Player();
        player.Start();
        await Ended(player);

        Assert.Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(40), 4), _time.Sleeps);
        var stats = player.Diagnostics.Snapshot();
        Assert.Equal(4, stats.Frames);
        Assert.Equal(0, stats.LateFrames);
    }

    [Fact]
    public async Task Slow_sends_are_counted_late_and_not_slept_for()
    {
        Proc(Nals.Frames(3));
        var player = Player();
        _sink.OnSend = n => _time.Advance(TimeSpan.FromMilliseconds(n == 2 ? 100 : 0));
        player.Start();
        await Ended(player);

        // Frame 2 finished 60 ms after its deadline; frame 3 is then behind too (no sleep), and the clock never resets.
        var stats = player.Diagnostics.Snapshot();
        Assert.Equal(2, stats.LateFrames);
        Assert.Equal(60, stats.MaxLatenessMs, 3);
        Assert.Equal(80, stats.LateTotalMs, 3);
        Assert.Equal([TimeSpan.FromMilliseconds(40)], _time.Sleeps);
    }

    [Fact]
    public async Task Packets_are_spread_over_the_packet_pace_fraction()
    {
        Proc(Nals.Frames(1));
        _sink.PacketsPerFrame = 4;
        var player = Player(o => o with { PacketPace = 0.5 });
        player.Start();
        await Ended(player);

        // 0.5 × 40 ms over 4 packets → 5 ms apart, then the remaining 20 ms of the frame interval.
        Assert.Equal(
            [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(20)],
            _time.Sleeps);
    }

    [Fact]
    public async Task Single_packet_frames_are_not_paced_and_pacing_stops_mid_frame_on_stop()
    {
        Proc(Nals.Frames(1));
        var player = Player(o => o with { PacketPace = 0.5 });
        player.Start();
        await Ended(player);
        Assert.Equal([TimeSpan.FromMilliseconds(40)], _time.Sleeps);

        _sink.PacketsPerFrame = 3;
        Proc(Nals.Frames(2), running: true);
        var stopping = Player(o => o with { PacketPace = 0.5 });
        _time.AutoFire = d => d >= TimeSpan.FromMilliseconds(10); // hold the per-packet sleeps
        var sent = _sink.PacketsSent;
        stopping.Start();
        await Waits.UntilAsync(() => _sink.PacketsSent == sent + 1, "first packet");
        stopping.Stop();
        Assert.Equal(VideoPlayerEnd.Stopped, await Ended(stopping));
        Assert.Equal(sent + 1, _sink.PacketsSent);
    }

    [Fact]
    public async Task Read_stalls_are_measured()
    {
        var proc = Proc(Nals.Frames(2), step: 8);
        proc.Stdout.OnRead = () => _time.Advance(TimeSpan.FromMilliseconds(3));
        var player = Player();
        player.Start();
        await Ended(player);
        var reads = (int)Math.Ceiling(Nals.Frames(2).Length / 8.0) + 1;
        Assert.Equal(reads * 3, player.Diagnostics.Snapshot().ReadStallMs, 3);
    }

    // ── pause ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pause_mid_stream_holds_then_resumes_without_bursting()
    {
        Proc([.. Nals.AnnexB(Nals.Idr, Nals.Aud, Nals.P1, Nals.Aud, Nals.P2, Nals.Aud), .. Nals.StartCode]);
        var player = Player();
        _sink.OnSend = n =>
        {
            if (n == 1)
            {
                player.PausePlayback();
            }
        };
        player.Start();

        await Waits.UntilAsync(() => player.HeldByPause, "pause hold");
        Assert.True(player.IsPaused);
        Assert.Same(player.Pause, ((Slopsoil.Streaming.Contracts.IMediaClock)player).Pause);
        Assert.Equal(1, _sink.Count);
        _time.Advance(TimeSpan.FromSeconds(10)); // a long pause
        player.ResumePlayback();

        Assert.Equal(VideoPlayerEnd.Natural, await Ended(player));
        Assert.False(player.IsPaused);
        AssertFrames([Nals.Idr], [Nals.P1], [Nals.P2]);
        Assert.Equal(0, player.Diagnostics.Snapshot().LateFrames); // the pacing clock was shifted by the pause
    }

    [Fact]
    public async Task Paused_before_the_first_frame()
    {
        Proc([.. Nals.AnnexB(Nals.Idr, Nals.Aud), .. Nals.StartCode]);
        var player = Player();
        player.PausePlayback();
        player.Start();
        await Waits.UntilAsync(() => player.HeldByPause, "pause hold");
        Assert.Empty(_sink.Frames);
        player.ResumePlayback();
        await Ended(player);
        AssertFrames([Nals.Idr]);
    }

    [Fact]
    public async Task Stop_while_paused()
    {
        var proc = Proc(Nals.AnnexB(Nals.Idr, Nals.Aud, Nals.P1, Nals.Aud), running: true);
        var player = Player();
        player.PausePlayback();
        player.Start();
        await Waits.UntilAsync(() => player.HeldByPause, "pause hold");
        player.Stop();
        Assert.Equal(VideoPlayerEnd.Stopped, await Ended(player));
        Assert.Empty(_sink.Frames);
        Assert.True(proc.Terminated >= 1);
    }

    // ── seek / in-place restart ──────────────────────────────────────────

    [Fact]
    public async Task Seek_while_paused_restarts_ffmpeg_at_the_target()
    {
        var first = Proc(Nals.AnnexB(Nals.Idr, Nals.Aud, Nals.P1, Nals.Aud), running: true);
        Proc(Nals.Frames(2));
        var player = Player();
        player.PausePlayback();
        player.Start();
        await Waits.UntilAsync(() => player.HeldByPause, "pause hold");

        player.Seek(12.0);
        Assert.Equal(12.0, player.Position);
        await Waits.UntilAsync(() => _runner.Started.Count == 2, "restart");
        await Waits.UntilAsync(() => player.HeldByPause, "pause hold after restart");
        Assert.Empty(_sink.Frames);
        Assert.Equal(["term", "close", "term"], first.Events.Take(3));
        Assert.Equal("12.000", Opt(_runner.Started.Last(), "-ss"));
        Assert.Equal(1, player.RestartSeq);
        Assert.Equal(12.0, player.SegmentOffset);

        player.ResumePlayback();
        Assert.Equal(VideoPlayerEnd.Natural, await Ended(player));
        Assert.Equal(2, _sink.Count);
        Assert.Equal(12.0 + (2 / 25.0), player.Position, 6);
        Assert.Contains("seeking to 12.0s", _log.Text);
    }

    [Fact]
    public async Task Seek_mid_stream_skips_remaining_frames_and_the_tail()
    {
        var first = Proc(Nals.AnnexB(Nals.Idr, Nals.Aud, Nals.P1, Nals.Aud, Nals.P2), running: true);
        Proc(Nals.Frames(3));
        var player = Player();
        _sink.OnSend = n =>
        {
            if (n == 1)
            {
                player.Seek(7.0);
            }
        };
        player.Start();

        Assert.Equal(VideoPlayerEnd.Natural, await Ended(player));
        Assert.Equal(Nals.Idr, Assert.Single(_sink.Frames[0]));
        Assert.Equal(4, _sink.Count); // IDR from the first process, then three from the second
        Assert.True(first.Terminated >= 1);
        Assert.Same(Sw, player.Encoder); // a seek restart is not an encoder fallback
        Assert.Equal(7.0 + (3 / 25.0), player.Position, 6);
    }

    [Fact]
    public async Task Seek_before_start_kills_the_fresh_ffmpeg_and_restarts()
    {
        var first = Proc(Nals.AnnexB(Nals.Idr, Nals.Aud, Nals.P1, Nals.Aud), running: true);
        Proc(Nals.Frames(1));
        var player = Player();
        player.Seek(-3); // clamped
        Assert.Equal(0.0, player.Position);
        Assert.True(player.SeekPending);
        player.Seek(40.0);
        Assert.Equal(40.0, player.Position);
        player.Start();

        await Ended(player);
        Assert.Equal(["close", "term"], first.Events);
        Assert.DoesNotContain("-ss", _runner.Started.First());
        Assert.Equal("40.000", Opt(_runner.Started.Last(), "-ss"));
        Assert.Single(_sink.Frames);
        Assert.False(player.SeekPending);
    }

    [Fact]
    public async Task Seek_after_the_natural_end_is_ignored()
    {
        Proc(Nals.Frames(1));
        var player = Player(o => o with { StartOffset = 10.0 });
        player.Start();
        await Ended(player);
        player.Seek(30.0);
        Assert.False(player.SeekPending);
        Assert.Equal(10.04, player.Position, 6);
        player.SetDebugOverlay(true);
        Assert.False(player.DebugOverlay);
    }

    [Fact]
    public async Task Seek_arriving_at_the_end_of_output_restarts()
    {
        var first = Proc(Nals.Frames(1));
        Proc(Nals.Frames(1));
        var player = Player();
        var once = false;
        _sink.OnSend = _ =>
        {
            if (!once)
            {
                once = true;
                first.Stdout.OnRead = () => player.Seek(20.0); // arrives just before stdout EOF
            }
        };
        player.Start();
        await Ended(player);
        Assert.Equal(2, _runner.Started.Count);
        Assert.Equal(20.04, player.Position, 6);
    }

    [Fact]
    public async Task Set_aspect_restarts_in_place_at_the_current_position()
    {
        Proc(Nals.Frames(250), blockAtEnd: true);
        Proc(Nals.Frames(1));
        var player = Player(o => o with { Encoder = Sw with { Vf = FfmpegFilters.FitScale("960:540") } });
        Assert.Null(player.Aspect);
        player.Start();
        await Waits.UntilAsync(() => player.SegmentFrames == 250, "250 frames");

        player.SetAspect(2.39);
        Assert.Equal(2.39, player.Aspect);
        await Ended(player);

        var cmd = _runner.Started.Last();
        Assert.Contains("2.3900000000", Opt(cmd, "-vf"));
        Assert.Equal("10.000", Opt(cmd, "-ss"));
    }

    // ── audio follower handshake ─────────────────────────────────────────

    [Fact]
    public async Task Restart_waits_for_the_audio_follower_to_drain()
    {
        _time.AutoFire = d => d != VideoPlayer.DrainWait;
        Proc(Nals.Frames(2), blockAtEnd: true);
        Proc(Nals.Frames(1));
        var player = Player();
        using var follower = player.RegisterAudioFollower();
        player.Start();
        await Waits.UntilAsync(() => _sink.Count == 2, "frames");

        player.Seek(5.0);
        await Waits.UntilAsync(() => player.RestartSeq == 1, "restart");
        Assert.True(player.SeekPending); // the restarting window counts as pending
        Assert.Equal(5.0, player.SegmentOffset);
        Assert.Equal(0, player.SegmentFrames);
        Assert.Single(_runner.Started); // the new FFmpeg waits for the drain

        follower.SignalDrained();
        Assert.Equal(VideoPlayerEnd.Natural, await Ended(player));
        Assert.Equal(2, _runner.Started.Count);
        Assert.False(player.SeekPending);
    }

    [Fact]
    public async Task Restart_wait_is_bounded()
    {
        _time.AutoFire = d => d != VideoPlayer.DrainWait;
        Proc(Nals.Frames(1), blockAtEnd: true);
        Proc(Nals.Frames(1));
        var player = Player();
        using var follower = player.RegisterAudioFollower();
        player.Start();
        await Waits.UntilAsync(() => _sink.Count == 1, "frames");
        player.Seek(5.0);
        await Waits.UntilAsync(() => player.RestartSeq == 1, "restart");
        Assert.Single(_runner.Started);

        await Waits.UntilAsync(() => _time.HeldPlayerTimers == 1, "drain wait");
        _time.Advance(VideoPlayer.DrainWait);
        await Ended(player);
        Assert.Equal(2, _runner.Started.Count);
    }

    [Fact]
    public async Task Unregistered_follower_is_not_waited_for()
    {
        _time.AutoFire = d => d != VideoPlayer.DrainWait;
        Proc(Nals.Frames(1), blockAtEnd: true);
        Proc(Nals.Frames(1));
        var player = Player();
        var stale = player.RegisterAudioFollower();
        var current = player.RegisterAudioFollower();
        stale.Dispose(); // not the registered one: no effect
        current.Dispose();
        player.Start();
        await Waits.UntilAsync(() => _sink.Count == 1, "frames");
        player.Seek(5.0);
        await Ended(player);
        Assert.Equal(1, player.RestartSeq);
    }

    [Fact]
    public async Task Stop_during_the_drain_wait_ends_promptly()
    {
        _time.AutoFire = d => d != VideoPlayer.DrainWait;
        Proc(Nals.Frames(1), blockAtEnd: true);
        var player = Player();
        using var follower = player.RegisterAudioFollower();
        player.Start();
        await Waits.UntilAsync(() => _sink.Count == 1, "frames");
        player.Seek(5.0);
        await Waits.UntilAsync(() => player.RestartSeq == 1, "restart");
        player.Stop();
        Assert.Equal(VideoPlayerEnd.Stopped, await Ended(player));
    }

    // ── stderr ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Stderr_startup_noise_is_demoted_and_lines_are_redacted()
    {
        Proc(Nals.Frames(1), stderr: "DecodeFrame failed\n\nreal problem at http://u:secret@h/x\n");
        var player = Player();
        player.Start();
        await Ended(player);
        await Waits.UntilAsync(() => _log.Text.Contains("ffmpeg video:", StringComparison.Ordinal), "stderr");

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Debug && e.Message == "ffmpeg (startup): DecodeFrame failed");
        var warning = Assert.Single(_log.Entries, e => e.Message.StartsWith("ffmpeg video:", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.DoesNotContain("secret", warning.Message);
    }

    // ── debug overlay ────────────────────────────────────────────────────

    [Fact]
    public async Task Debug_overlay_from_the_start_writes_the_file_before_ffmpeg()
    {
        Proc(Nals.Frames(1), blockAtEnd: true);
        var player = Player(o => o with { DebugOverlay = true });
        string? atStart = null;
        _runner.OnStart = _ => atStart = File.ReadAllText(player.OverlayPath);
        player.Start();
        await Waits.UntilAsync(() => _sink.Count == 1, "frame");

        Assert.True(player.DebugOverlay);
        Assert.Equal(Path.Combine(_dir.FullName, "slopsoil_1001_debug.txt"), player.OverlayPath);
        Assert.Contains("drawtext=textfile=" + player.OverlayPath + ":reload=1", Opt(_runner.Started.Single(), "-vf"));
        Assert.Contains("fontsize=18", Opt(_runner.Started.Single(), "-vf")); // 720 / 40
        Assert.NotNull(atStart);
        foreach (var group in new[] { "VIDEO", "A/V", "STREAM", "NETWORK" })
        {
            Assert.Contains(group, atStart);
        }

        // Refreshed once per second from the live snapshot.
        File.Delete(player.OverlayPath);
        _time.Advance(VideoPlayer.OverlayRefresh);
        Assert.Contains("VIDEO", File.ReadAllText(player.OverlayPath));

        player.Stop();
        await Ended(player);
        Assert.False(File.Exists(player.OverlayPath));
    }

    [Fact]
    public async Task Toggling_the_overlay_restarts_in_place()
    {
        Proc(Nals.Frames(25), blockAtEnd: true);
        Proc(Nals.Frames(25), blockAtEnd: true);
        Proc(Nals.Frames(1));
        var player = Player();
        player.Start();
        await Waits.UntilAsync(() => player.SegmentFrames == 25, "frames");
        Assert.DoesNotContain("drawtext", Opt(_runner.Started.Single(), "-vf"));
        _time.Advance(VideoPlayer.OverlayRefresh);
        Assert.False(File.Exists(player.OverlayPath)); // no overlay work while off

        player.SetDebugOverlay(true);
        player.SetDebugOverlay(true); // no-op
        await Waits.UntilAsync(() => player.RestartSeq == 1 && player.SegmentFrames == 25, "frames after restart");
        var on = _runner.Started.Last();
        Assert.Contains("drawtext", Opt(on, "-vf"));
        Assert.Equal("1.000", Opt(on, "-ss"));
        Assert.True(File.Exists(player.OverlayPath));

        player.SetDebugOverlay(false);
        await Ended(player);
        Assert.Equal(3, _runner.Started.Count);
        Assert.DoesNotContain("drawtext", Opt(_runner.Started.Last(), "-vf"));
        Assert.Equal("2.000", Opt(_runner.Started.Last(), "-ss"));
        Assert.Equal(2, player.RestartSeq);
    }

    [Fact]
    public async Task Overlay_write_failures_are_tolerated()
    {
        Proc(Nals.Frames(1), blockAtEnd: true);
        var player = Player(o => o with { DebugOverlay = true });
        player.Start();
        await Waits.UntilAsync(() => _sink.Count == 1, "frame");
        File.Delete(player.OverlayPath);
        Directory.CreateDirectory(player.OverlayPath); // the atomic replace now fails
        _time.Advance(VideoPlayer.OverlayRefresh);
        Assert.Contains("overlay write failed", _log.Text);
        Directory.Delete(player.OverlayPath);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(player.OverlayPath)!, Path.GetFileName(player.OverlayPath) + "*.tmp"));
    }

    [Fact]
    public async Task Stats_are_logged_every_ten_seconds_while_streaming()
    {
        Proc(Nals.Frames(1), blockAtEnd: true);
        var player = Player();
        player.Start();
        await Waits.UntilAsync(() => _sink.Count == 1, "frame");
        _time.Advance(DiagnosticsLogger.Interval);
        Assert.Single(_log.Entries, e => e.Message.StartsWith("video stats:", StringComparison.Ordinal));
        player.Stop();
        await Ended(player);
        Assert.Equal(2, _log.Entries.Count(e => e.Message.StartsWith("video stats:", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("1920:1080", 27)]
    [InlineData("1280:720", 18)]
    [InlineData("320:240", 12)]
    [InlineData("bogus", 24)]
    public void Overlay_font_size_follows_the_output_height(string resolution, int size) =>
        Assert.Equal(size, VideoPlayer.OverlayFontSize(resolution));

    [Fact]
    public async Task Shared_diagnostics_take_the_player_as_clock()
    {
        var diagnostics = new StreamDiagnostics(25, "1280:720", "1k", _time);
        Proc(Nals.Frames(5));
        var player = Player(o => o with { StartOffset = 3 }, diagnostics);
        Assert.Same(diagnostics, player.Diagnostics);
        player.Start();
        await Ended(player);
        Assert.Equal(3.2, diagnostics.Snapshot().Position, 6);
        Assert.Equal(25.0, player.Fps);
    }
}

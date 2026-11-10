using Microsoft.Extensions.Logging;
using JellySend.Streaming.Process;
using JellySend.Streaming.Video;

namespace JellySend.Tests.Streaming;

public sealed class FfmpegCommandTests
{
    private static readonly EncoderConfig Hw = new("h264_vaapi", ["-vaapi_device", "/dev/x"], ["-b:v", "1k"], "hwupload");
    private static readonly EncoderConfig Sw = new("libopenh264", [], ["-b:v", "1k"], "scale=1:1");

    private static FfmpegCommandOptions Options(string url = "http://user:pw@host:8080/live.ts", EncoderConfig? enc = null) =>
        new() { Url = url, Encoder = enc ?? Sw, AudioFifo = "/tmp/jellysend_1001_audio.fifo", Fps = 25.0 };

    private static string Opt(IReadOnlyList<string> cmd, string flag) => cmd[IndexOf(cmd, flag) + 1];

    private static int IndexOf(IReadOnlyList<string> cmd, string value) => cmd.ToList().IndexOf(value);

    [Fact]
    public void Audio_track_maps_that_audio_track_of_the_file_and_is_ignored_without_audio()
    {
        var cmd = FfmpegCommand.Build(Options() with { AudioTrack = 3 });
        Assert.Equal(["-map", "0:a:3", "-af"], cmd.TakeLast(13).Take(3));
        var silent = FfmpegCommand.Build(Options() with { AudioTrack = 3, HasAudio = false });
        Assert.DoesNotContain("0:a:3", silent);
        Assert.Contains("1:a:0", silent);
    }

    [Fact]
    public void Url_live_command()
    {
        var cmd = FfmpegCommand.Build(Options("https://h/live.m3u8", Hw) with { Live = true, ProbeSize = 123 });

        Assert.Equal("ffmpeg", cmd[0]);
        Assert.Equal(["-y", "-loglevel", "warning", "-progress", "pipe:2", "-stats_period", "2", "-max_error_rate", "1"], cmd.Skip(1).Take(9));
        Assert.DoesNotContain("-re", cmd);
        Assert.Equal("1", Opt(cmd, "-reconnect"));
        Assert.Equal("1", Opt(cmd, "-reconnect_streamed"));
        Assert.Equal("5", Opt(cmd, "-reconnect_delay_max"));
        Assert.Equal("123", Opt(cmd, "-probesize"));
        Assert.Equal("123", Opt(cmd, "-analyzeduration"));
        Assert.Equal("+discardcorrupt", Opt(cmd, "-fflags"));
        Assert.Equal("/dev/x", Opt(cmd, "-vaapi_device"));
        Assert.True(IndexOf(cmd, "-vaapi_device") < IndexOf(cmd, "-i"));
        Assert.Equal("https://h/live.m3u8", Opt(cmd, "-i"));
        Assert.Equal("0:v:0", Opt(cmd, "-map"));
        Assert.Equal("h264_vaapi", Opt(cmd, "-c:v"));
        Assert.Equal("hwupload", Opt(cmd, "-vf"));
        Assert.Equal("1k", Opt(cmd, "-b:v"));
        Assert.Equal("25", Opt(cmd, "-r"));
        Assert.Equal("25", Opt(cmd, "-g"));
        Assert.Equal("h264", Opt(cmd, "-f"));
        Assert.Contains("pipe:1", cmd);
        Assert.Contains("0:a:0", cmd);
        Assert.DoesNotContain("anullsrc=r=48000:cl=stereo", cmd);
        Assert.Equal(
            ["-map", "0:a:0", "-af", "aresample=async=1:first_pts=0", "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2", "-f", "s16le", "/tmp/jellysend_1001_audio.fifo"],
            cmd.TakeLast(13));
        // One media timeline (spec 004): constant-rate video from output time 0, before the rate.
        Assert.Equal("cfr", Opt(cmd, "-fps_mode"));
        Assert.Equal(IndexOf(cmd, "-r") - 2, IndexOf(cmd, "-fps_mode"));
    }

    [Fact]
    public void Local_vod_uses_re_and_no_reconnect()
    {
        var cmd = FfmpegCommand.Build(Options("/media/movie.mkv") with { Live = false });
        Assert.Contains("-re", cmd);
        Assert.True(IndexOf(cmd, "-re") < IndexOf(cmd, "-i"));
        Assert.DoesNotContain("-reconnect", cmd);
    }

    [Fact]
    public void Url_vod_has_no_re()
    {
        var cmd = FfmpegCommand.Build(Options("rtsp://cam/stream") with { Live = false });
        Assert.DoesNotContain("-re", cmd);
        Assert.Contains("-reconnect", cmd);
    }

    [Theory]
    [InlineData("http://h/x", true)]
    [InlineData("https://h/x", true)]
    [InlineData("rtmp://h/x", true)]
    [InlineData("rtsp://h/x", true)]
    [InlineData("/dev/video0", false)]
    [InlineData("file:///x.mkv", false)]
    public void Reconnect_flags_only_for_urls(string url, bool expected) =>
        Assert.Equal(expected, FfmpegCommand.Build(Options(url)).Contains("-reconnect"));

    [Fact]
    public void Local_input_with_unknown_liveness_is_paced()
    {
        Assert.Contains("-re", FfmpegCommand.Build(Options("/dev/video0") with { Live = null }));
    }

    [Fact]
    public void No_audio_injects_silence_after_the_video_output()
    {
        var cmd = FfmpegCommand.Build(Options() with { HasAudio = false });
        Assert.Contains("anullsrc=r=48000:cl=stereo", cmd);
        Assert.Contains("1:a:0", cmd);
        Assert.DoesNotContain("0:a:0", cmd);
        Assert.Equal("lavfi", cmd[IndexOf(cmd, "anullsrc=r=48000:cl=stereo") - 2]);
        Assert.True(IndexOf(cmd, "pipe:1") < IndexOf(cmd, "anullsrc=r=48000:cl=stereo"));
        Assert.True(IndexOf(cmd, "anullsrc=r=48000:cl=stereo") < IndexOf(cmd, "1:a:0"));
        Assert.Equal(FfmpegCommand.AudioFilter, Opt(cmd, "-af")); // generated silence is aligned like real audio
    }

    [Fact]
    public void Start_offset_seeks_before_the_input()
    {
        var cmd = FfmpegCommand.Build(Options("https://h/x.m3u8") with { Offset = 90.5 });
        Assert.Equal("90.500", Opt(cmd, "-ss"));
        Assert.True(IndexOf(cmd, "-ss") < IndexOf(cmd, "-i"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void No_seek_at_the_start(double offset) =>
        Assert.DoesNotContain("-ss", FfmpegCommand.Build(Options() with { Offset = offset }));

    [Theory]
    [InlineData(29.97, "29.97", "30")]
    [InlineData(59.94, "59.94", "60")]
    [InlineData(23.976, "23.976", "24")]
    [InlineData(24000.0 / 1001.0, "24000/1001", "24")]
    [InlineData(30000.0 / 1001.0, "30000/1001", "30")]
    [InlineData(25.0, "25", "25")]
    [InlineData(30.0, "30", "30")]
    [InlineData(0.4, "0.4", "1")]
    [InlineData(2.5, "2.5", "2")] // banker's rounding, like Python round()
    public void Output_rate_is_exact_and_gop_is_rounded(double fps, string rate, string gop)
    {
        var cmd = FfmpegCommand.Build(Options() with { Fps = fps });
        Assert.Equal(rate, Opt(cmd, "-r"));
        Assert.Equal(gop, Opt(cmd, "-g"));
    }

    [Fact]
    public void Forced_aspect_replaces_dar_in_the_scaler()
    {
        var enc = Sw with { Vf = FfmpegFilters.FitScale("960:540") };
        var vf = Opt(FfmpegCommand.Build(Options(enc: enc) with { Aspect = 16.0 / 9 }), "-vf");
        Assert.Contains("1.7777777778", vf);
        Assert.DoesNotContain("dar", vf);
    }

    [Fact]
    public void Fit_scale_expression()
    {
        Assert.Equal(
            "scale=w='if(gt(dar,1920/1080),1920,2*round(1080*dar/2))':h='if(gt(dar,1920/1080),2*round(1920/dar/2),1080)',setsar=1",
            FfmpegFilters.FitScale("1920:1080"));
        var vaapi = FfmpegFilters.FitScale("960:540", "scale_vaapi");
        Assert.StartsWith("scale_vaapi=w='if(gt(dar,960/540),960,", vaapi);
        Assert.EndsWith(",setsar=1", vaapi);
    }

    [Fact]
    public void With_aspect()
    {
        var vf = FfmpegFilters.FitScale("960:540");
        Assert.Equal(vf, FfmpegFilters.WithAspect(vf, null));
        var forced = FfmpegFilters.WithAspect(FfmpegFilters.FitScale("960:540", "scale_vaapi"), 4.0 / 3);
        Assert.DoesNotContain("dar", forced);
        Assert.Contains("1.3333333333", forced);
        Assert.StartsWith("scale_vaapi=", forced);
    }

    [Fact]
    public void No_overlay_filter_when_debug_is_off()
    {
        var enc = EncoderConfig.ForLibx264("1280:720", "1M");
        Assert.Equal(enc.Vf, Opt(FfmpegCommand.Build(Options(enc: enc)), "-vf"));
        Assert.DoesNotContain(FfmpegCommand.Build(Options(enc: enc)), a => a.Contains("drawtext", StringComparison.Ordinal));
    }

    [Fact]
    public void Overlay_is_drawn_after_the_software_scaler()
    {
        var enc = EncoderConfig.ForLibx264("1280:720", "1M");
        var vf = Opt(FfmpegCommand.Build(Options(enc: enc) with { OverlayFile = "/tmp/jellysend_7_debug.txt", OverlayFontSize = 18, Aspect = 2.0 }), "-vf");

        Assert.StartsWith(FfmpegFilters.WithAspect(enc.Vf, 2.0) + ",drawtext=", vf);
        Assert.Contains("textfile=/tmp/jellysend_7_debug.txt:reload=1", vf);
        Assert.Contains("font=monospace", vf);
        Assert.Contains("fontsize=18", vf);
        Assert.Contains("box=1:boxcolor=black@0.6", vf);
        Assert.Contains("x=12:y=12", vf);
        Assert.Contains("expansion=none", vf);
    }

    [Fact]
    public void Vaapi_overlay_is_drawn_on_downloaded_frames_before_the_final_hwupload()
    {
        var enc = EncoderConfig.ForVaapi("1280:720", "1M");
        var vf = Opt(FfmpegCommand.Build(Options(enc: enc) with { OverlayFile = "/tmp/o.txt" }), "-vf");

        Assert.StartsWith(enc.Vf + ",hwdownload,format=nv12,drawtext=textfile=/tmp/o.txt:", vf);
        Assert.EndsWith(",format=nv12,hwupload", vf);
        Assert.True(vf.IndexOf("scale_vaapi", StringComparison.Ordinal) < vf.IndexOf("drawtext", StringComparison.Ordinal));
    }

    [Fact]
    public void Overlay_path_is_escaped_for_the_filter_graph()
    {
        Assert.Equal("/tmp/a\\\\:b\\,c", FfmpegCommand.Escape("/tmp/a:b,c"));
        Assert.Equal("it\\\\\\'s", FfmpegCommand.Escape("it's"));
        Assert.Equal("/tmp/plain_1.txt", FfmpegCommand.Escape("/tmp/plain_1.txt"));
    }

    [Fact]
    public void Custom_executable()
    {
        Assert.Equal("/opt/ffmpeg", FfmpegCommand.Build(Options() with { Executable = "/opt/ffmpeg" })[0]);
    }

    // ── encoder detection ──────────────────────────────────────────────────

    [Theory]
    [InlineData("h264_nvenc h264_vaapi libx264", "h264_nvenc", "h264_nvenc")]
    [InlineData("h264_nvenc h264_vaapi libx264", "h264_vaapi", "h264_vaapi")]
    [InlineData("h264_nvenc h264_vaapi libx264", "libx264", "libx264")]
    [InlineData("h264_vaapi libopenh264", "libopenh264", "libopenh264")]
    [InlineData("h264_nvenc libx264 libopenh264", "", null)]
    [InlineData("", "libx264", null)]
    public void Detection_prefers_nvenc_then_vaapi_then_libx264_then_libopenh264(string available, string working, string? expected)
    {
        var probed = new List<string>();
        var runner = ProbeRunner(available, name =>
        {
            probed.Add(name);
            return working.Split(' ').Contains(name);
        });

        var selection = new EncoderDetector(runner).Detect("1280:720", "10000k");

        Assert.Equal(expected, selection?.Primary.Name);
        if (expected == "h264_vaapi")
        {
            Assert.Equal(["-vaapi_device", "/dev/dri/renderD128"], selection!.Primary.PreInput);
            Assert.StartsWith("format=nv12,hwupload,scale_vaapi=", selection.Primary.Vf);
        }

        if (expected is not null)
        {
            Assert.Contains("-b:v", selection!.Primary.PostCodec);
            Assert.Equal("10000k", selection.Primary.PostCodec[selection.Primary.PostCodec.ToList().IndexOf("-b:v") + 1]);
        }

        // Each listed encoder is probed in order until one works.
        string[] order = ["h264_nvenc", "h264_vaapi", "libx264", "libopenh264"];
        Assert.Equal(probed, order.Where(probed.Contains));
    }

    [Theory]
    [InlineData("h264_vaapi")]
    [InlineData("h264_nvenc")]
    public void Hardware_encoders_disable_b_frames(string name)
    {
        var selection = new EncoderDetector(ProbeRunner(name, n => n == name)).Detect("1920:1080", "1M");
        var post = selection!.Primary.PostCodec.ToList();
        Assert.Equal(name, selection.Primary.Name);
        Assert.True(selection.Primary.IsHardware);
        Assert.Equal("0", post[post.IndexOf("-bf") + 1]);
    }

    [Theory]
    [InlineData("h264_vaapi libx264 libopenh264", "h264_vaapi", "libopenh264")]
    [InlineData("h264_nvenc libx264", "h264_nvenc", "libx264")]
    [InlineData("libx264 libopenh264", "libx264", "libopenh264")]
    [InlineData("libx264", "libx264", null)]
    [InlineData("libopenh264", "libopenh264", null)]
    [InlineData("h264_nvenc", "h264_nvenc", null)]
    public void Software_fallback(string available, string primary, string? fallback)
    {
        var selection = new EncoderDetector(ProbeRunner(available, n => n == primary)).Detect("1280:720", "1M");
        Assert.Equal(primary, selection!.Primary.Name);
        Assert.Equal(fallback, selection.Fallback?.Name);
    }

    [Fact]
    public void Detection_without_ffmpeg_returns_null()
    {
        var log = new VideoTestLogger();
        var runner = new FakeProcessRunner { OnRun = _ => null };
        Assert.Null(new EncoderDetector(runner, log).Detect("1280:720", "1M"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("ffmpeg not found", StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_uses_the_real_filter_chain_and_reports_failures()
    {
        var log = new VideoTestLogger();
        var runner = new FakeProcessRunner
        {
            OnRun = args => new ProcessResult(args.Contains("good") ? 0 : 1, "", "a\nb\nno device http://u:secret@h/x\n"),
        };
        var detector = new EncoderDetector(runner, log);

        Assert.True(detector.TestEncoder("good", [], "hwupload"));
        Assert.Equal(
            ["ffmpeg", "-hide_banner", "-f", "lavfi", "-i", "nullsrc=size=64x64:rate=1", "-vframes", "1", "-vf", "hwupload", "-c:v", "good", "-f", "null", "-"],
            runner.Runs[^1]);
        Assert.False(detector.TestEncoder("bad", ["-x"]));
        Assert.DoesNotContain("-vf", runner.Runs[^1]);
        Assert.Equal("-x", runner.Runs[^1][2]);
        var failure = Assert.Single(log.Entries, e => e.Message.StartsWith("encoder bad probe failed (exit 1)", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, failure.Level);
        Assert.Contains("a | b | no device", failure.Message);
        Assert.DoesNotContain("secret", failure.Message);
    }

    [Fact]
    public void Probe_errors_count_as_unavailable()
    {
        var runner = new FakeProcessRunner { OnRun = _ => throw new TimeoutException("slow") };
        Assert.False(new EncoderDetector(runner).TestEncoder("x", []));
        Assert.False(new EncoderDetector(new FakeProcessRunner { OnRun = _ => null }).TestEncoder("x", []));
    }

    [Fact]
    public void Encoder_listing_timeout_disables_video()
    {
        var runner = new FakeProcessRunner { OnRun = _ => throw new TimeoutException("slow") };
        Assert.Null(new EncoderDetector(runner).Detect("1280:720", "1M"));
    }

    [Fact]
    public void Software_configs_use_the_profile()
    {
        var cfg = EncoderConfig.ForLibopenh264("1280:720", "10000k");
        Assert.Equal(FfmpegFilters.FitScale("1280:720"), cfg.Vf);
        Assert.Equal("10000k", cfg.PostCodec[cfg.PostCodec.ToList().IndexOf("-b:v") + 1]);
        Assert.False(cfg.IsHardware);
        var x264 = EncoderConfig.ForLibx264("1280:720", "2M");
        Assert.Contains("aud=1", x264.PostCodec);
        Assert.Contains("zerolatency", x264.PostCodec);
    }

    private static FakeProcessRunner ProbeRunner(string available, Func<string, bool> works) => new()
    {
        OnRun = args => args.Contains("-encoders")
            ? new ProcessResult(0, available, "")
            : new ProcessResult(works(args[args.ToList().IndexOf("-c:v") + 1]) ? 0 : 1, "", "probe failed"),
    };
}

/// <summary>The scaler and encoder probes against a real FFmpeg (skipped when it is missing).</summary>
public sealed class FfmpegFilterRealTests
{
    private static readonly bool HaveFfmpeg = Probe("ffmpeg") && Probe("ffprobe");

    [Theory]
    [InlineData("1920x1080", "1", 960, 540)] // 16:9 fills the box
    [InlineData("1440x1080", "1", 720, 540)] // 4:3 → pillarbox-sized, not stretched
    [InlineData("1920x800", "1", 960, 400)] // 2.4:1 → letterbox-sized
    [InlineData("720x480", "32/27", 960, 540)] // anamorphic 16:9 DVD
    public void Fit_scale_preserves_aspect_ratio(string src, string sar, int w, int h)
    {
        Assert.SkipUnless(HaveFfmpeg, "needs ffmpeg and ffprobe");
        Assert.Equal((w, h), Render($"testsrc=size={src}:duration=0.1,setsar={sar}", FfmpegFilters.FitScale("960:540")));
    }

    [Theory]
    [InlineData("1440x1080", 16.0 / 9, 960, 540)] // 4:3 stretched to fill 16:9
    [InlineData("1920x1080", 4.0 / 3, 720, 540)] // 16:9 squeezed to 4:3
    [InlineData("1920x1080", 2.39, 960, 402)] // stretched to scope
    public void Forced_aspect(string src, double aspect, int w, int h)
    {
        Assert.SkipUnless(HaveFfmpeg, "needs ffmpeg and ffprobe");
        Assert.Equal((w, h), Render($"testsrc=size={src}:duration=0.1", FfmpegFilters.WithAspect(FfmpegFilters.FitScale("960:540"), aspect)));
    }

    [Fact]
    public void Real_detection_finds_a_working_encoder()
    {
        Assert.SkipUnless(HaveFfmpeg, "needs ffmpeg");
        var selection = new EncoderDetector(SystemProcessRunner.Instance).Detect("1280:720", "2M");
        Assert.SkipWhen(selection is null, "no working H.264 encoder in this ffmpeg");
        Assert.Contains(selection!.Primary.Name, new[] { "h264_nvenc", "h264_vaapi", "libx264", "libopenh264" });
    }

    [Fact]
    public void Overlay_filter_runs_in_real_ffmpeg()
    {
        Assert.SkipUnless(HaveFfmpeg, "needs ffmpeg");
        var dir = Directory.CreateTempSubdirectory("jellysend:overlay,x");
        try
        {
            var file = Path.Combine(dir.FullName, "debug.txt");
            File.WriteAllText(file, "VIDEO 30/30 fps 100%\nA/V drift +3 ms");
            var vf = FfmpegFilters.FitScale("320:180") + "," + FfmpegCommand.DrawText(file, 12);
            var result = SystemProcessRunner.Instance.Run(
                ["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=640x360:duration=0.2", "-vf", vf, "-f", "null", "-"],
                TimeSpan.FromSeconds(30));
            Assert.True(result.ExitCode == 0, result.StandardError);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static (int, int) Render(string source, string vf)
    {
        var dir = Directory.CreateTempSubdirectory("jellysend-scale");
        try
        {
            var output = Path.Combine(dir.FullName, "f.png");
            var r = SystemProcessRunner.Instance.Run(
                ["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", source, "-vf", vf, "-frames:v", "1", "-y", output],
                TimeSpan.FromSeconds(30));
            Assert.True(r.ExitCode == 0, r.StandardError);
            var probe = SystemProcessRunner.Instance.Run(
                ["ffprobe", "-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0", output], TimeSpan.FromSeconds(30));
            var parts = probe.StandardOutput.Trim().Split(',');
            return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static bool Probe(string exe)
    {
        try
        {
            return SystemProcessRunner.Instance.Run([exe, "-version"], TimeSpan.FromSeconds(10)).ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

using Microsoft.Extensions.Logging;
using Slopsoil.Streaming.Diagnostics;
using Slopsoil.Tests.Discord;

namespace Slopsoil.Tests.Streaming;

public sealed class DiagnosticsLoggerTests
{
    private static readonly StreamDiagnosticsSnapshot Sample = new()
    {
        FpsSent = 29.94,
        FpsTarget = 30,
        LateFrames = 2,
        MaxLatenessMs = 12.4,
        ReadStallMs = 5.2,
        PacketsPerSec = 412.3,
        SendDrops = 1,
        SendErrors = 3,
        DriftMs = 4.6,
        SilenceSteps = 1,
        SkipSteps = 2,
        Encoder = "libx264",
        Resolution = "1920:1080",
        Bitrate = "12000k",
        SourceCodec = "hevc",
        Position = 3723.9,
        Runtime = 5400,
        Window = TimeSpan.FromSeconds(10),
        Frames = 299,
        LateTotalMs = 20.2,
    };

    [Fact]
    public void Log_line_matches_the_python_video_stats_fields()
    {
        Assert.Equal(
            "video stats: 29.9 fps (target 30) | late 2/299 frames (max 12 ms, total 20 ms) | ffmpeg read-block 5 ms / 10.0s "
            + "| 4123 pkts | drops 1, errors 3 | drift +5 ms, silence 1, skip 2 | libx264 1920:1080 12000k src=hevc pos 1:02:03/1:30:00",
            DiagnosticsFormat.LogLine(Sample));
    }

    [Fact]
    public void Log_line_without_audio_or_source_info()
    {
        var line = DiagnosticsFormat.LogLine(Sample with { DriftMs = null, SourceCodec = null, Runtime = null, Position = -1 });
        Assert.Contains("drift n/a", line);
        Assert.EndsWith("src=? pos 0:00:00/?", line);
    }

    [Fact]
    public void Log_line_is_redacted()
    {
        var line = DiagnosticsFormat.LogLine(Sample with { SourceCodec = "http://u:secret@h/x?api_key=k3y" });
        Assert.DoesNotContain("secret", line);
        Assert.DoesNotContain("k3y", line);
    }

    [Fact]
    public void Overlay_has_the_four_groups()
    {
        var lines = DiagnosticsFormat.Overlay(Sample with { DriftMs = -7.2 }).Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("VIDEO   29.9/30 fps  late 2 (max 12 ms)  read-stall 5 ms  412 pkt/s", lines[0]);
        Assert.Equal("A/V     drift -7 ms  silence 1  skip 2", lines[1]);
        Assert.Equal("STREAM  libx264 1920x1080 12000k  src hevc  1:02:03 / 1:30:00", lines[2]);
        Assert.Equal("NETWORK drops 1  errors 3", lines[3]);
        Assert.Contains("drift 0 ms", DiagnosticsFormat.Overlay(Sample with { DriftMs = 0.2 }));
    }

    [Fact]
    public void Logs_every_ten_seconds_and_once_more_on_dispose()
    {
        var time = new ManualTimeProvider();
        var log = new VideoTestLogger();
        var diagnostics = new StreamDiagnostics(30, "1280:720", "1M", time);
        var logger = new DiagnosticsLogger(diagnostics, time, log);

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(log.Entries);
        diagnostics.VideoFrameSent(TimeSpan.FromMilliseconds(3));
        time.Advance(TimeSpan.FromSeconds(1));
        var first = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, first.Level);
        Assert.StartsWith("video stats: 0.1 fps (target 30) | late 0/1 frames", first.Message);

        time.Advance(DiagnosticsLogger.Interval);
        Assert.Equal(2, log.Entries.Count);

        logger.Dispose();
        logger.Dispose();
        Assert.Equal(3, log.Entries.Count);
        time.Advance(DiagnosticsLogger.Interval);
        Assert.Equal(3, log.Entries.Count);
    }

    [Fact]
    public void A_failing_tick_is_logged_and_the_timer_keeps_running()
    {
        var time = new ManualTimeProvider();
        var log = new VideoTestLogger();
        var ticks = 0;
        using var timer = new RepeatingTimer(time, TimeSpan.FromSeconds(1), () => throw new InvalidOperationException("tick " + ++ticks), ex => log.Entries.Enqueue((LogLevel.Warning, ex.Message)));
        time.Advance(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["tick 1", "tick 2"], log.Entries.Select(e => e.Message));
    }

    [Fact]
    public void Overlay_file_is_replaced_atomically()
    {
        var dir = Directory.CreateTempSubdirectory("slopsoil-ovl");
        try
        {
            var path = Path.Combine(dir.FullName, "o.txt");
            OverlayFile.Write(path, "one");
            OverlayFile.Write(path, "two");
            Assert.Equal("two", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
            OverlayFile.Delete(path);
            OverlayFile.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

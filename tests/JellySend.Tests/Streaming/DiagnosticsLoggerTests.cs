using Microsoft.Extensions.Logging;
using JellySend.Streaming.Diagnostics;
using JellySend.Tests.Discord;

namespace JellySend.Tests.Streaming;

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
        VideoBufferSeconds = 4.96,
        AudioBufferSeconds = 5.02,
        Encoder = "libx264",
        Resolution = "1920:1080",
        Bitrate = "12000k",
        SourceCodec = "hevc",
        Source = "Jellyfin transcode (burning in subtitles)",
        JellyfinStats = "→h264 1920x1080 8.1 Mbit/s 72 fps hw vaapi audio →aac",
        EncodeSpeed = 1.0,
        EncodeFps = 30,
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
            "video stats: 29.9 fps (target 30, src ?) | late 2/299 frames (max 12 ms, total 20 ms) | ffmpeg read-block 5 ms / 10.0s "
            + "| 4123 pkts | drops 1, errors 3 | drift +5 ms, silence 1, skip 2 | buffer video 5.0 s, audio 5.0 s "
            + "| jellyfin Jellyfin transcode (burning in subtitles): →h264 1920x1080 8.1 Mbit/s 72 fps hw vaapi audio →aac "
            + "| ffmpeg hevc → libx264 1920x1080 12000k  speed 1.00x  30 fps | pos 1:02:03/1:30:00",
            DiagnosticsFormat.LogLine(Sample));
    }

    [Fact]
    public void Log_line_without_audio_or_source_info()
    {
        var line = DiagnosticsFormat.LogLine(Sample with
        {
            DriftMs = null, SourceCodec = null, Source = null, Runtime = null, Position = -1, EncodeSpeed = null, EncodeFps = null,
        });
        Assert.Contains("drift n/a", line);
        Assert.EndsWith("| jellyfin ? | ffmpeg ? → libx264 1920x1080 12000k  speed ? | pos 0:00:00/?", line);
    }

    [Fact]
    public void Log_line_is_redacted()
    {
        var line = DiagnosticsFormat.LogLine(Sample with { SourceCodec = "http://u:secret@h/x?api_key=k3y" });
        Assert.DoesNotContain("secret", line);
        Assert.DoesNotContain("k3y", line);
    }

    [Fact]
    public void Overlay_has_the_four_groups_and_the_buffer()
    {
        var lines = DiagnosticsFormat.Overlay(Sample with { DriftMs = -7.2, VideoBufferSeconds = 2.5 }).Split('\n');
        Assert.Equal(7, lines.Length);
        Assert.Equal("VIDEO    29.9/30 fps (src ?)  late 2 (max 12 ms)  read-stall 5 ms  412 pkt/s", lines[0]);
        Assert.Equal("A/V      drift -7 ms  silence 1  skip 2", lines[1]);
        Assert.Equal("BUFFER   video 2.5 s  audio 5.0 s", lines[2]);
        Assert.Equal("JELLYFIN Jellyfin transcode (burning in subtitles): →h264 1920x1080 8.1 Mbit/s 72 fps hw vaapi audio →aac", lines[3]);
        Assert.Equal("FFMPEG   hevc → libx264 1920x1080 12000k  speed 1.00x  30 fps", lines[4]);
        Assert.Equal("STREAM   1:02:03 / 1:30:00  (encoded twice: Jellyfin + bot)", lines[5]);
        Assert.Equal("NETWORK  drops 1  errors 3", lines[6]);
        Assert.Contains("drift 0 ms", DiagnosticsFormat.Overlay(Sample with { DriftMs = 0.2 }));
        var direct = DiagnosticsFormat.Overlay(Sample with { Source = StreamDiagnostics.OriginalFile });
        Assert.Contains("JELLYFIN original file — not transcoded", direct);
        Assert.Contains("(encoded once: bot)", direct);
        Assert.Contains("JELLYFIN Jellyfin transcode (x): stats not reported", DiagnosticsFormat.Overlay(Sample with { Source = "Jellyfin transcode (x)", JellyfinStats = null }));
        Assert.Contains("JELLYFIN ?", DiagnosticsFormat.Overlay(Sample with { Source = null }));
        Assert.Contains("(encodes ?)", DiagnosticsFormat.Overlay(Sample with { Source = null }));
        Assert.Contains("speed 0.80x  19 fps  dup 3 drop 1", DiagnosticsFormat.Overlay(Sample with { EncodeSpeed = 0.8, EncodeFps = 19, EncodeDupFrames = 3, EncodeDropFrames = 1 }));
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
        Assert.StartsWith("video stats: 0.1 fps (target 30, src ?) | late 0/1 frames", first.Message);

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
        var dir = Directory.CreateTempSubdirectory("jellysend-ovl");
        try
        {
            var path = Path.Combine(dir.FullName, "o.txt");
            OverlayFile.Write(path, "one");
            OverlayFile.Write(path, "two");
            Assert.Equal("two", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(dir.FullName, "*.tmp"));
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

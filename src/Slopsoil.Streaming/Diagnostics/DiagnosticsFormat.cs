using System.Globalization;
using Slopsoil.Common.Logging;

namespace Slopsoil.Streaming.Diagnostics;

/// <summary>Text renderings of a <see cref="StreamDiagnosticsSnapshot"/>: the log line and the on-stream overlay.</summary>
public static class DiagnosticsFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The periodic log line: the Python "video stats" fields first, then audio sync, network and stream info.
    /// Redacted (the source codec / encoder names are free text).
    /// </summary>
    public static string LogLine(StreamDiagnosticsSnapshot s) => Redactor.RedactText(string.Create(
        Inv,
        $"video stats: {s.FpsSent:F1} fps (target {s.FpsTarget:0.##}, src {SourceFps(s)}) | late {s.LateFrames}/{s.Frames} frames "
        + $"(max {s.MaxLatenessMs:F0} ms, total {s.LateTotalMs:F0} ms) | ffmpeg read-block {s.ReadStallMs:F0} ms / {s.Window.TotalSeconds:F1}s "
        + $"| {Math.Round(s.PacketsPerSec * s.Window.TotalSeconds):F0} pkts | drops {s.SendDrops}, errors {s.SendErrors} "
        + $"| drift {Drift(s.DriftMs)}, silence {s.SilenceSteps}, skip {s.SkipSteps} "
        + $"| buffer video {s.VideoBufferSeconds:F1} s, audio {s.AudioBufferSeconds:F1} s "
        + $"| {s.Encoder} {s.Resolution} {s.Bitrate} src={s.SourceCodec ?? "?"} pos {Clock(s.Position)}/{Runtime(s.Runtime)} "
        + $"| source {Source(s)}"));

    /// <summary>The overlay: the four groups of spec US6 scenario 1 plus the read-ahead (spec 004), one per line.</summary>
    public static string Overlay(StreamDiagnosticsSnapshot s) => string.Create(
        Inv,
        $"VIDEO   {s.FpsSent:F1}/{s.FpsTarget:0.##} fps (src {SourceFps(s)})  late {s.LateFrames} (max {s.MaxLatenessMs:F0} ms)  "
        + $"read-stall {s.ReadStallMs:F0} ms  {s.PacketsPerSec:F0} pkt/s\n"
        + $"A/V     drift {Drift(s.DriftMs)}  silence {s.SilenceSteps}  skip {s.SkipSteps}\n"
        + $"BUFFER  video {s.VideoBufferSeconds:F1} s  audio {s.AudioBufferSeconds:F1} s\n"
        + $"STREAM  {s.Encoder} {s.Resolution.Replace(':', 'x')} {s.Bitrate}  src {s.SourceCodec ?? "?"}  "
        + $"{Clock(s.Position)} / {Runtime(s.Runtime)}\n"
        + $"SOURCE  {Source(s)}\n"
        + $"NETWORK drops {s.SendDrops}  errors {s.SendErrors}");

    /// <summary>Where the picture comes from and how often it is encoded: Discord always gets the bot's encode.</summary>
    private static string Source(StreamDiagnosticsSnapshot s) => s.Source switch
    {
        null => "?",
        StreamDiagnostics.OriginalFile => "original file → encoded once (bot)",
        var other => $"{other} → encoded twice (Jellyfin + bot)",
    };

    private static string SourceFps(StreamDiagnosticsSnapshot s) => s.SourceFps is { } f ? Video.FrameRate.Format(f) : "?";

    private static string Drift(double? ms) => ms is { } d ? d.ToString("+0;-0;0", Inv) + " ms" : "n/a";

    private static string Runtime(double? seconds) => seconds is { } r ? Clock(r) : "?";

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return string.Create(Inv, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}");
    }
}

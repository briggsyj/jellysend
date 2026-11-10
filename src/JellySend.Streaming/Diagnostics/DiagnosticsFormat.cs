using System.Globalization;
using JellySend.Common.Logging;

namespace JellySend.Streaming.Diagnostics;

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
        + $"| jellyfin {Jellyfin(s)} | ffmpeg {Ffmpeg(s)} | pos {Clock(s.Position)}/{Runtime(s.Runtime)}"));

    /// <summary>
    /// The overlay: playout (VIDEO, A/V, BUFFER), then each step of the chain (spec 017): JELLYFIN (original file or
    /// its transcode, with its stats), FFMPEG (the bot's decode → encode for Discord, with its speed), the position, and
    /// NETWORK (sending to Discord).
    /// </summary>
    public static string Overlay(StreamDiagnosticsSnapshot s) => string.Create(
        Inv,
        $"VIDEO    {s.FpsSent:F1}/{s.FpsTarget:0.##} fps (src {SourceFps(s)})  late {s.LateFrames} (max {s.MaxLatenessMs:F0} ms)  "
        + $"read-stall {s.ReadStallMs:F0} ms  {s.PacketsPerSec:F0} pkt/s\n"
        + $"A/V      drift {Drift(s.DriftMs)}  silence {s.SilenceSteps}  skip {s.SkipSteps}\n"
        + $"BUFFER   video {s.VideoBufferSeconds:F1} s  audio {s.AudioBufferSeconds:F1} s\n"
        + $"JELLYFIN {Jellyfin(s)}\n"
        + $"FFMPEG   {Ffmpeg(s)}\n"
        + $"STREAM   {Clock(s.Position)} / {Runtime(s.Runtime)}  ({Encodes(s)})\n"
        + $"NETWORK  drops {s.SendDrops}  errors {s.SendErrors}");

    /// <summary>Step 1: Jellyfin hands over the original file, or transcodes it first (why, and its live stats).</summary>
    private static string Jellyfin(StreamDiagnosticsSnapshot s) => s.Source switch
    {
        null => "?",
        StreamDiagnostics.OriginalFile => "original file — not transcoded",
        var other => $"{other}: {s.JellyfinStats ?? "stats not reported"}",
    };

    /// <summary>Step 2: the bot's FFmpeg decodes the input and encodes for Discord; speed ≈ 1.00x once the read-ahead is full.</summary>
    private static string Ffmpeg(StreamDiagnosticsSnapshot s)
    {
        var text = string.Create(Inv, $"{s.SourceCodec ?? "?"} → {s.Encoder} {s.Resolution.Replace(':', 'x')} {s.Bitrate}");
        text += s.EncodeSpeed is { } speed ? string.Create(Inv, $"  speed {speed:0.00}x") : "  speed ?";
        if (s.EncodeFps is { } fps)
        {
            text += string.Create(Inv, $"  {fps:0} fps");
        }

        if (s.EncodeDupFrames is > 0 || s.EncodeDropFrames is > 0)
        {
            text += string.Create(Inv, $"  dup {s.EncodeDupFrames ?? 0} drop {s.EncodeDropFrames ?? 0}");
        }

        return text;
    }

    /// <summary>How many times the picture is encoded before Discord gets it.</summary>
    private static string Encodes(StreamDiagnosticsSnapshot s) => s.Source switch
    {
        null => "encodes ?",
        StreamDiagnostics.OriginalFile => "encoded once: bot",
        _ => "encoded twice: Jellyfin + bot",
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

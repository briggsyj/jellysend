using System.Globalization;

namespace JellySend.Streaming.Diagnostics;

/// <summary>One CSV row per 10 s diagnostics snapshot for the soak harness (T049, tests/soak/README.md).</summary>
public static class SoakCsv
{
    public const string Header =
        "elapsed_s,position_s,window_s,frames,fps_sent,fps_target,late_frames,late_total_ms,max_lateness_ms,read_stall_ms,"
        + "packets_per_s,send_drops,send_errors,drift_ms,silence_steps,skip_steps,encoder";

    public static string Row(double elapsedSeconds, StreamDiagnosticsSnapshot s) => string.Join(
        ',',
        F(elapsedSeconds),
        F(s.Position),
        F(s.Window.TotalSeconds),
        s.Frames.ToString(CultureInfo.InvariantCulture),
        F(s.FpsSent),
        F(s.FpsTarget),
        s.LateFrames.ToString(CultureInfo.InvariantCulture),
        F(s.LateTotalMs),
        F(s.MaxLatenessMs),
        F(s.ReadStallMs),
        F(s.PacketsPerSec),
        s.SendDrops.ToString(CultureInfo.InvariantCulture),
        s.SendErrors.ToString(CultureInfo.InvariantCulture),
        s.DriftMs is { } d ? F(d) : "",
        s.SilenceSteps.ToString(CultureInfo.InvariantCulture),
        s.SkipSteps.ToString(CultureInfo.InvariantCulture),
        s.Encoder.Replace(',', ';'));

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

using System.Globalization;
using JellySend.Streaming.Diagnostics;

namespace JellySend.Streaming.Video;

/// <summary>
/// Reads FFmpeg's <c>-progress</c> blocks (<c>key=value</c> lines ending in <c>progress=continue|end</c>) from stderr
/// and publishes the encode fps, speed and dropped / duplicated frames to the diagnostics (spec 017).
/// </summary>
internal sealed class FfmpegProgress(StreamDiagnostics diagnostics)
{
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "frame", "fps", "bitrate", "total_size", "out_time_us", "out_time_ms", "out_time", "dup_frames", "drop_frames", "speed", "progress",
    };

    private double? _fps;
    private double? _speed;
    private long? _dup;
    private long? _drop;

    /// <summary>A new FFmpeg run: forget the last block.</summary>
    public void Reset() => (_fps, _speed, _dup, _drop) = (null, null, null, null);

    /// <summary>True when <paramref name="line"/> is a progress line (consumed; not to be logged).</summary>
    public bool Feed(string line)
    {
        var eq = line.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return false;
        }

        var key = line[..eq];
        if (!Keys.Contains(key) && !key.StartsWith("stream_", StringComparison.Ordinal))
        {
            return false;
        }

        var value = line[(eq + 1)..].Trim();
        switch (key)
        {
            case "fps":
                _fps = Double(value);
                break;
            case "speed":
                _speed = Double(value.TrimEnd('x'));
                break;
            case "dup_frames":
                _dup = Long(value);
                break;
            case "drop_frames":
                _drop = Long(value);
                break;
            case "progress":
                diagnostics.FfmpegProgress(_fps, _speed, _dup, _drop);
                break;
        }

        return true;
    }

    private static double? Double(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : null;

    private static long? Long(string s) =>
        long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
}

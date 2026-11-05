using System.Globalization;

namespace JellySend.Streaming.Video;

/// <summary>
/// A runtime output height (spec 010, <c>!resolution 480p</c>): the 16:9 box for it, and a bitrate scaled down from
/// the configured one by pixel count, so a smaller picture also uses less bandwidth.
/// </summary>
public static class OutputSize
{
    /// <summary>Lowest bitrate a scaled-down stream gets, in kbit/s.</summary>
    public const int MinKbps = 300;

    /// <summary>The 16:9 bounding box <c>W:H</c> for <paramref name="height"/>, with an even width (480 → 854:480).</summary>
    public static string Box(int height)
    {
        var width = (int)Math.Round(height * 16.0 / 9.0 / 2.0, MidpointRounding.AwayFromZero) * 2;
        return string.Create(CultureInfo.InvariantCulture, $"{width}:{height}");
    }

    /// <summary>
    /// Resolution and bitrate for <paramref name="height"/> given the configured ones: the box for the height, and the
    /// configured bitrate times the pixel ratio when the box is smaller (never more than configured, never below
    /// <see cref="MinKbps"/>). An unparsable bitrate is kept as configured.
    /// </summary>
    public static (string Resolution, string Bitrate) For(int height, string configuredResolution, string configuredBitrate)
    {
        var box = Box(height);
        if (Pixels(box) is not { } target || Pixels(configuredResolution) is not { } configured || ParseKbps(configuredBitrate) is not { } kbps)
        {
            return (box, configuredBitrate);
        }

        if (target >= configured)
        {
            return (box, configuredBitrate);
        }

        var scaled = Math.Max(MinKbps, (int)Math.Round(kbps * (double)target / configured));
        return (box, string.Create(CultureInfo.InvariantCulture, $"{Math.Min(scaled, kbps)}k"));
    }

    /// <summary>A bitrate like <c>4000k</c>, <c>4M</c>, <c>4.5m</c> or <c>4000000</c> in kbit/s; null when unparsable.</summary>
    public static int? ParseKbps(string bitrate)
    {
        var t = bitrate.Trim();
        if (t.Length == 0)
        {
            return null;
        }

        var (number, factor) = char.ToLowerInvariant(t[^1]) switch
        {
            'k' => (t[..^1], 1.0),
            'm' => (t[..^1], 1000.0),
            _ => (t, 0.001),
        };
        return double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v > 0
            ? (int)Math.Round(v * factor)
            : null;
    }

    private static long? Pixels(string resolution)
    {
        var parts = resolution.Split(':', 'x');
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            && w > 0 && h > 0 ? w * h : null;
    }
}

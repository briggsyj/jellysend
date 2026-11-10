using System.Globalization;

namespace JellySend.Streaming.Video;

/// <summary>
/// Output frame-rate selection (spec 007): <c>STREAM_FPS</c> is a ceiling, the stream follows the source rate.
/// </summary>
public static class FrameRate
{
    /// <summary>NTSC rates (n·1000/1001) are snapped to their exact value when a reported rate is this close.</summary>
    private const double SnapTolerance = 0.01;

    /// <summary>Rates outside this range are treated as unknown (VFR containers report e.g. 1000 or 90000).</summary>
    private const double Min = 1.0;

    private const double Max = 240.0;

    private static readonly int[] NtscBases = [24, 30, 48, 60, 120];

    /// <summary>
    /// A plausible source rate, snapped to the exact NTSC value (23.976 → 24000/1001) or to a whole number when
    /// within <see cref="SnapTolerance"/>; null when unknown or implausible.
    /// </summary>
    public static double? Normalize(double? reported)
    {
        if (reported is not { } r || double.IsNaN(r) || r < Min || r > Max)
        {
            return null;
        }

        foreach (var n in NtscBases)
        {
            var ntsc = n * 1000.0 / 1001.0;
            if (Math.Abs(r - ntsc) < SnapTolerance)
            {
                return ntsc;
            }
        }

        var whole = Math.Round(r);
        return Math.Abs(r - whole) < SnapTolerance ? whole : r;
    }

    /// <summary>
    /// The output rate: the source rate when it is at or below <paramref name="ceiling"/>; above it, the source
    /// rate divided by the smallest whole number that fits under the ceiling (60 → 30, 50 → 25, 59.94 → 29.97), so
    /// every n-th frame is kept evenly. Unknown source → the ceiling.
    /// </summary>
    public static double Choose(double? source, double ceiling)
    {
        if (Normalize(source) is not { } s)
        {
            return ceiling;
        }

        if (s <= ceiling + SnapTolerance)
        {
            return s;
        }

        var divisor = Math.Ceiling((s / ceiling) - 1e-9);
        return s / divisor;
    }

    /// <summary>FFmpeg <c>-r</c> value: an exact rational for NTSC rates (<c>24000/1001</c>), else the number.</summary>
    public static string ToFfmpeg(double fps)
    {
        var thousandths = fps * 1001.0;
        var rounded = Math.Round(thousandths);
        if (Math.Abs(thousandths - rounded) < 1e-6 && rounded % 1000 == 0 && Math.Abs(fps - Math.Round(fps)) > 1e-6)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(long)rounded}/1001");
        }

        return fps.ToString("G6", CultureInfo.InvariantCulture);
    }

    /// <summary>Display form for the overlay / logs: two decimals, trailing zeros dropped (23.98, 25, 29.97).</summary>
    public static string Format(double fps) => fps.ToString("0.##", CultureInfo.InvariantCulture);
}

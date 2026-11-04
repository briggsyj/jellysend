using System.Globalization;
using System.Text.RegularExpressions;

namespace Slopsoil.Commands.Media;

/// <summary>One <c>!media aspect-ratio</c> choice: label and forced display ratio (null = the media's own).</summary>
public sealed record AspectOption(string Label, double? Ratio);

/// <summary>Pure text helpers and constants of <c>cogs/jellyfin.py</c>.</summary>
public static partial class MediaText
{
    public const int PlaylistMax = 10;

    public static readonly IReadOnlyList<AspectOption> AspectOptions =
    [
        new("Original (default) — keep the media's own shape", null),
        new("16:9 — widescreen", 16.0 / 9.0),
        new("4:3 — classic TV", 4.0 / 3.0),
        new("2.39:1 — cinema scope (21:9)", 2.39),
        new("1.85:1 — theatrical widescreen", 1.85),
    ];

    public const string HelpText = """
        **🎬 slopsoil**

        **Play**
        `!play <title>` — e.g. `!play inception`
        `!play <show> s03e05` — an episode (also `3x5`, `season 3 episode 5`)
        `!play <show>` — pick season → episode · `!play <show> s03` — pick from season 3
        `!play <title> 1:23:45` — start at a time · `!play "<title>"` — quotes: all title

        **Control**
        `!pause` · `!play` (resume) · `!skip`
        `!time` — where we are · `!time 1:23:45` — jump (`1:23` = 1h 23m; also `45m`, `90s`)
        `!stop` — stop, stay in voice · `!leave` — stop and leave · `!join` — join your voice channel
        `!subs` — subtitles (`!subs english`, `!subs off`) · `!audio` — audio track (`!audio japanese`)
        `!ar` — picture shape (`!ar 16:9`) · `!debug on` / `off` — stats on the stream

        **Playlist** (max 10, plays in order)
        `!queue <title>` or `!queue <show> s01e02`
        `!playlist` · `!order <#> <new #>` · `!remove <#>`

        -# Every command also works as `!media <command>`. `!help <command>` for details.
        """;

    /// <summary>
    /// Index into <see cref="AspectOptions"/> named by <paramref name="text"/> (spec 008): its number, its ratio
    /// (<c>16:9</c>, <c>2.39</c>, <c>21:9</c>) or a word of it (<c>widescreen</c>, <c>scope</c>, <c>original</c>). Null when none.
    /// </summary>
    public static int? FindAspect(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t.Length == 0)
        {
            return null;
        }

        if (t.All(char.IsAsciiDigit))
        {
            return int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= AspectOptions.Count ? n - 1 : null;
        }

        return t switch
        {
            "original" or "default" or "off" or "reset" or "none" or "auto" or "source" => 0,
            "16:9" or "16x9" or "16/9" or "1.78" or "1.78:1" or "1.77" or "widescreen" or "wide" or "hd" => 1,
            "4:3" or "4x3" or "4/3" or "1.33" or "1.33:1" or "classic" or "tv" or "square" or "fullscreen" => 2,
            "2.39" or "2.39:1" or "2.35" or "2.35:1" or "2.4" or "2.40" or "21:9" or "21x9" or "scope" or "cinemascope" or "cinema" or "ultrawide" => 3,
            "1.85" or "1.85:1" or "theatrical" or "flat" => 4,
            _ => null,
        };
    }

    /// <summary>Appended to <see cref="HelpText"/> when SyncPlay is configured; <c>{0}</c> is the group name.</summary>
    public const string SyncPlayHelp =
        "\n\n**Jellyfin SyncPlay** — join **{0}** in a Jellyfin app to pause, play and seek this stream together.";

    /// <summary>
    /// Jump-to time in seconds, or null if <paramref name="text"/> isn't one: <c>1:23:45</c>, <c>1.23.45</c>,
    /// <c>1:23</c> (h:mm), bare number = minutes, or units <c>1h30m</c> / <c>45m</c> / <c>90s</c>.
    /// Components after the first must be &lt; 60.
    /// </summary>
    public static double? ParseTimestamp(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (t.Length == 0)
        {
            return null;
        }

        if (Clock().IsMatch(t))
        {
            var parts = t.Split(':', '.').Select(Long).ToArray();
            if (parts.Skip(1).Any(p => p >= 60))
            {
                return null;
            }

            if (parts.Length == 1)
            {
                return parts[0] * 60.0;
            }

            var s = parts.Length == 3 ? parts[2] : 0;
            return (parts[0] * 3600.0) + (parts[1] * 60.0) + s;
        }

        var u = Units().Match(t);
        if (u.Success && (u.Groups[1].Success || u.Groups[2].Success || u.Groups[3].Success))
        {
            double G(int i) => u.Groups[i].Success ? Long(u.Groups[i].Value) : 0;
            return (G(1) * 3600.0) + (G(2) * 60.0) + G(3);
        }

        return null;
    }

    /// <summary><c>H:MM:SS</c>, negatives clamp to zero, fractions truncate.</summary>
    public static string FormatTimestamp(double seconds)
    {
        var total = (long)Math.Max(0, Math.Truncate(seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600}:{total % 3600 / 60:00}:{total % 60:00}");
    }

    // Python ints are unbounded; clamp absurd inputs instead of overflowing.
    private static long Long(string s) => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : long.MaxValue / 3600;

    [GeneratedRegex(@"^[0-9]+(?:[:.][0-9]{1,2}){0,2}$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^(?:([0-9]+)h)?(?:([0-9]+)m)?(?:([0-9]+)s)?$")]
    private static partial Regex Units();
}

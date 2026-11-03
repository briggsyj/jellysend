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
        **🎬 Jellyfin — `!media`**

        **Play**
        `!media <movie>` — e.g. `!media inception`
        `!media <show> s03e05` — a specific episode
        `!media <show>` — pick season → episode
        `!media <movie> 1:23:45` — start at a time (also `<show> s03e05 45m`)

        **Control**
        `!media pause` · `!media play`
        `!media resume 1:23:45` — jump to a time (`1:23` = 1h 23m; also `45m`, `90s`)
        `!media skip` · `!media stop` (stops and leaves voice)
        `!media aspect-ratio` — change the picture shape

        **Playlist** (max 10, plays in order)
        `!media queue <movie>` or `!media queue <show> s01e02`
        `!media playlist`
        `!media order <#> <new #>` · `!media remove <#>`

        -# Title starts with queue, order or remove? Use `!media search <title>`.
        """;

    /// <summary>Appended to <see cref="HelpText"/> when SyncPlay is configured; <c>{0}</c> is the group name.</summary>
    public const string SyncPlayHelp =
        "\n\n**Jellyfin SyncPlay** — join **{0}** in a Jellyfin app to pause, play and seek this stream together.";

    /// <summary>Split <c>"show name s02e01"</c> into ("show name", 2, 1); no code → (query, null, null). First code wins.</summary>
    public static (string Show, int? Season, int? Episode) ParseEpisodeQuery(string query)
    {
        var m = EpisodeCode().Match(query);
        if (!m.Success)
        {
            return (query, null, null);
        }

        return (query[..m.Index].Trim(), Int(m.Groups[1].Value), Int(m.Groups[2].Value));
    }

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

    /// <summary>
    /// <c>!media &lt;title&gt; &lt;time&gt;</c> (spec 002, FR-002/FR-003): split a trailing start time off a query. Only the last
    /// word counts, only after a non-empty title, and only if it has a <c>:</c>/<c>.</c> separator or an
    /// <c>h</c>/<c>m</c>/<c>s</c> unit and is a valid <see cref="ParseTimestamp"/> time — a bare number is a title
    /// (<c>blade runner 2049</c>), and a time on its own is a title (<c>2:22</c>).
    /// </summary>
    public static (string Title, double? Seconds) SplitTrailingTime(string query)
    {
        var trimmed = query.Trim();
        var split = trimmed.LastIndexOfAny([' ', '\t', '\n']);
        if (split < 0)
        {
            return (trimmed, null);
        }

        var title = trimmed[..split].TrimEnd();
        var word = trimmed[(split + 1)..];
        var marked = word.Contains(':', StringComparison.Ordinal) || word.Contains('.', StringComparison.Ordinal)
            || word.Any(ch => ch is 'h' or 'm' or 's' or 'H' or 'M' or 'S');
        if (title.Length == 0 || !marked || word.All(char.IsAsciiDigit) || ParseTimestamp(word) is not { } seconds)
        {
            return (trimmed, null);
        }

        return (title, seconds);
    }

    /// <summary><c>H:MM:SS</c>, negatives clamp to zero, fractions truncate.</summary>
    public static string FormatTimestamp(double seconds)
    {
        var total = (long)Math.Max(0, Math.Truncate(seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600}:{total % 3600 / 60:00}:{total % 60:00}");
    }

    private static int Int(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);

    // Python ints are unbounded; clamp absurd inputs instead of overflowing.
    private static long Long(string s) => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : long.MaxValue / 3600;

    [GeneratedRegex(@"\bs([0-9]+)e([0-9]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeCode();

    [GeneratedRegex(@"^[0-9]+(?:[:.][0-9]{1,2}){0,2}$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^(?:([0-9]+)h)?(?:([0-9]+)m)?(?:([0-9]+)s)?$")]
    private static partial Regex Units();
}

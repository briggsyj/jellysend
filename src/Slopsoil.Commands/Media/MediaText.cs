using System.Globalization;
using System.Text.RegularExpressions;

namespace Slopsoil.Commands.Media;

/// <summary>One <c>!media aspect-ratio</c> choice: label and forced display ratio (null = the media's own).</summary>
public sealed record AspectOption(string Label, double? Ratio);

/// <summary>One <c>!resolution</c> choice: label and output height (null = the configured resolution).</summary>
public sealed record ResolutionOption(string Label, int? Height);

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

    /// <summary>
    /// <c>!resolution</c> choices (spec 010): output height, 16:9 box, bitrate scaled down with the pixels; null = the
    /// configured <c>STREAM_RESOLUTION</c> / <c>STREAM_QUALITY</c>.
    /// </summary>
    public static readonly IReadOnlyList<ResolutionOption> ResolutionOptions =
    [
        new("Default — the configured resolution", null),
        new("360p", 360),
        new("480p", 480),
        new("540p", 540),
        new("720p", 720),
        new("1080p", 1080),
    ];

    /// <summary>Index into <see cref="ResolutionOptions"/>: its number, <c>720p</c> / <c>720</c>, <c>1280x720</c>, <c>hd</c>, <c>default</c>.</summary>
    public static int? FindResolution(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (t.Length == 0)
        {
            return null;
        }

        if (t.Length == 1 && char.IsAsciiDigit(t[0]))
        {
            var n = t[0] - '0';
            return n >= 1 && n <= ResolutionOptions.Count ? n - 1 : null;
        }

        t = t switch
        {
            "default" or "reset" or "auto" or "original" or "off" or "config" or "configured" => "default",
            "sd" => "480",
            "qhd" => "540",
            "hd" => "720",
            "fhd" or "fullhd" => "1080",
            _ => t,
        };
        if (t == "default")
        {
            return 0;
        }

        if (t.Contains('x', StringComparison.Ordinal) || t.Contains(':', StringComparison.Ordinal))
        {
            t = t.Split('x', ':')[^1];
        }

        t = t.TrimEnd('p');
        var index = ResolutionOptions.ToList().FindIndex(o => o.Height?.ToString(CultureInfo.InvariantCulture) == t);
        return index >= 0 ? index : null;
    }

    public const string HelpText = """
        ### 📺 Slopsoil — now broadcasting
        **▶️ Watch**
        `!play <title>` — find a film or show and play it
        `!play <show> s2e5` — jump straight to an episode
        `!play <show> s1e1-3` — several episodes in a row
        `!play <title> 1:20:00` — start partway through
        ### ⏯️ While watching
        `!pause` — pause
        `!play` — carry on
        `!time 1:20:00` — jump to a time
        `!time` — where are we?
        `!skip` — next in the playlist
        `!stop` — stop (stay in voice)
        `!leave` — stop and leave
        ### 📜 Playlist
        `!queue <title>` — add to the end (pick several episodes: `2,5,8`)
        `!playlist` — what's on and up next, numbered
        `!remove 2` — take item 2 off the playlist
        `!order 3 1` — move item 3 to the top (position 1)
        ### 🎛️ Picture & sound
        `!language japanese english` — audio + subtitles shortcut: set both at once
        `!subs` — subtitles: shows the choices · `!subs english`, `!subs off`
        `!audio` — audio language: shows the choices · `!audio japanese`
        `!ar` — aspect ratio (picture shape): shows the choices · `!ar 16:9`
        `!res` — resolution (picture size): shows the choices · `!res 720p`
        `!debug on` — stream stats on screen
        -# More on any command: `!help play` · everything in detail: `!help all` · every command also works as `!media <command>`.
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

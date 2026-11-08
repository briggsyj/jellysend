using System.Text.RegularExpressions;

namespace JellySend.Commands.Events;

/// <summary>
/// Spec 028: the playlist written in a Discord event's description. A line <c>Screenings</c> (any case, colon optional;
/// spec 037: also Screening, Showing(s), Playlist, Play list, Runlist, Run list, Run-list, after up to three words)
/// starts it; each following line is one item, written like <c>!play</c> (title, <c>show s1e1-3</c>, …), numbered,
/// bulleted or plain. The list ends at a blank line once it has items, or at the end of the description.
/// </summary>
public static partial class Screenings
{
    /// <summary>The items, in order; null when the description has no <c>Screenings</c> line (not the bot's event).</summary>
    public static IReadOnlyList<string>? Parse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var lines = description.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, IsMarker);
        if (start < 0)
        {
            return null;
        }

        var items = new List<string>();
        foreach (var line in lines.Skip(start + 1))
        {
            var text = Bullet().Replace(line, "").Trim();
            if (text.Length == 0)
            {
                if (items.Count > 0)
                {
                    break;
                }

                continue;
            }

            items.Add(text);
        }

        return items;
    }

    /// <summary>
    /// Spec 037: a line that starts the list — one of the <see cref="Keywords"/> at the end of a short line (up to three
    /// words before it: "Now showing:", "Movie playlist", "Tonight's screenings"), any case, colon optional, emoji or
    /// markdown around it. Never a bulleted or numbered line (an item can't start a list).
    /// </summary>
    public static bool IsMarker(string line) => !Bullet().IsMatch(line) && Marker().IsMatch(line);

    /// <summary>The words that start a list (spec 037), for the help.</summary>
    public static readonly IReadOnlyList<string> Keywords =
        ["Screening", "Screenings", "Showing", "Showings", "Playlist", "Play list", "Runlist", "Run list", "Run-list"];

    // Up to three words, then the keyword, then an optional colon; anything that isn't a letter or digit around it.
    [GeneratedRegex(
        @"^\W*(?:[\p{L}\p{N}'’]+\s+){0,3}(?:screenings?|showings?|playlist|play\s+list|runlist|run[\s-]list)\s*:?\W*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    // "1.", "1)", "-", "*", "•" (and markdown bold/italic around them) at the start of a line.
    [GeneratedRegex(@"^\s*(?:\d{1,3}\s*[.)]|[-*•–—])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Bullet();
}

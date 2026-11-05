using System.Text.RegularExpressions;

namespace JellySend.Commands.Events;

/// <summary>
/// Spec 028: the playlist written in a Discord event's description. A line <c>Screenings</c> (any case, colon optional)
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
        var start = Array.FindIndex(lines, l => Marker().IsMatch(l));
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

    // "Screenings", "screenings:", "**Screenings:**", "🎬 Screenings" — the word alone on its line.
    [GeneratedRegex(@"^\W*screenings\s*:?\W*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    // "1.", "1)", "-", "*", "•" (and markdown bold/italic around them) at the start of a line.
    [GeneratedRegex(@"^\s*(?:\d{1,3}\s*[.)]|[-*•–—])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Bullet();
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Slopsoil.Commands.Media;

/// <summary>
/// What a casual <c>!play</c> / <c>!queue</c> argument means (spec 008), recognised by format in any order: an episode
/// (<c>s3e5</c>, <c>3x5</c>, <c>season 3 episode 5</c>, <c>episode 5 season 3</c>), a season (<c>s3</c>,
/// <c>season 3</c>), a start time (<c>1:23:45</c>, <c>45m</c>), and the rest as the title. Quoted text is always title.
/// </summary>
/// <param name="Title">The title to search; empty when only formats were given.</param>
/// <param name="Season">Season number, if given.</param>
/// <param name="Episode">Episode number, if given (always with a season).</param>
/// <param name="Start">Start time in seconds, if given (only when parsed with times).</param>
/// <param name="Quoted">The title came (partly) from quotes: never fall back to a whole-text search.</param>
/// <param name="Text">The whole argument as typed (quotes removed), for the full-text fallback search.</param>
public sealed partial record PlayQuery(string Title, int? Season, int? Episode, double? Start, bool Quoted, string Text)
{
    /// <summary>Something besides the title was recognised (so a whole-text fallback search can differ).</summary>
    public bool HasFormats => Season is not null || Start is not null;

    /// <summary>The whole text as a plain title search (the fallback when the formats found nothing).</summary>
    public PlayQuery AsPlainTitle() => new(Text, null, null, null, Quoted, Text);

    /// <summary>Parse <paramref name="text"/>; <paramref name="withTime"/> = false leaves time-like words in the title (<c>!queue</c>).</summary>
    public static PlayQuery Parse(string text, bool withTime = true)
    {
        var (quoted, rest) = SplitQuotes(text.Trim());
        var plain = string.Join(' ', ((string[])[quoted ?? "", rest]).Where(s => s.Length > 0));

        int? season = null;
        int? episode = null;
        foreach (var pattern in (Regex[])[SxxEyy(), NxM(), SeasonEpisode(), EpisodeSeason()])
        {
            var m = pattern.Match(rest);
            if (m.Success)
            {
                var swap = pattern == EpisodeSeason();
                season = Int(m.Groups[swap ? 2 : 1].Value);
                episode = Int(m.Groups[swap ? 1 : 2].Value);
                rest = Cut(rest, m);
                break;
            }
        }

        if (season is null)
        {
            foreach (var pattern in (Regex[])[SeasonCode(), SeasonWord()])
            {
                var m = pattern.Match(rest);
                if (m.Success)
                {
                    season = Int(m.Groups[1].Value);
                    rest = Cut(rest, m);
                    break;
                }
            }
        }

        double? start = null;
        var words = rest.Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (withTime)
        {
            for (var i = 0; i < words.Count; i++)
            {
                if (IsTimeWord(words[i]) is { } seconds)
                {
                    start = seconds;
                    words.RemoveAt(i);
                    break;
                }
            }
        }

        var title = string.Join(' ', ((string[])[quoted ?? "", .. words]).Where(s => s.Length > 0));
        return new PlayQuery(title, season, episode, start, quoted is not null, plain);
    }

    /// <summary>
    /// A start time word: a valid <see cref="MediaText.ParseTimestamp"/> time with a <c>:</c>/<c>.</c> separator or an
    /// <c>h</c>/<c>m</c>/<c>s</c> unit — a bare number is a title word (<c>blade runner 2049</c>).
    /// </summary>
    public static double? IsTimeWord(string word)
    {
        var marked = word.Contains(':', StringComparison.Ordinal) || word.Contains('.', StringComparison.Ordinal)
            || word.Any(ch => ch is 'h' or 'm' or 's' or 'H' or 'M' or 'S');
        return marked && !word.All(char.IsAsciiDigit) && word.Any(char.IsAsciiDigit) ? MediaText.ParseTimestamp(word) : null;
    }

    /// <summary>A human form of what was asked: <c>The Office S02E03</c>, <c>The Office season 2</c>.</summary>
    public string Describe()
    {
        var sb = new StringBuilder(Title);
        if (Season is { } s)
        {
            sb.Append(Episode is { } e
                ? string.Create(CultureInfo.InvariantCulture, $" S{s:00}E{e:00}")
                : string.Create(CultureInfo.InvariantCulture, $" season {s}"));
        }

        return sb.ToString().Trim();
    }

    /// <summary>First quoted segment ("…", '…' at word edges, or “…”) and the text around it.</summary>
    private static (string? Quoted, string Other) SplitQuotes(string text)
    {
        var m = Quotes().Match(text);
        if (!m.Success)
        {
            return (null, text);
        }

        var inner = (m.Groups[1].Success ? m.Groups[1] : m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value.Trim();
        var rest = (text[..m.Index] + " " + text[(m.Index + m.Length)..]).Trim();
        return inner.Length == 0 ? (null, rest) : (inner, rest);
    }

    private static string Cut(string text, Match m) =>
        Spaces().Replace((text[..m.Index] + " " + text[(m.Index + m.Length)..]).Trim(), " ");

    private static int Int(string s) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : int.MaxValue;

    [GeneratedRegex(@"\bs([0-9]+)e([0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SxxEyy();

    [GeneratedRegex(@"\b([0-9]+)x([0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NxM();

    [GeneratedRegex(@"\bseason\s+([0-9]+)\s+episode\s+([0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"\bepisode\s+([0-9]+)\s+season\s+([0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeSeason();

    [GeneratedRegex(@"\bs([0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonCode();

    [GeneratedRegex(@"\bseason\s+([0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonWord();

    [GeneratedRegex("\"([^\"]*)\"|(?<!\\w)'([^']*)'(?!\\w)|“([^”]*)”")]
    private static partial Regex Quotes();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

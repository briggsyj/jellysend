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
    /// <summary>
    /// Several episodes asked for at once (spec 018): <c>s1e1-3</c>, <c>s1e8-s2e2</c>, <c>1x1-3</c>, <c>s1e1,3,5</c>,
    /// <c>season 1 episodes 1-3</c> / <c>1 to 3</c> / <c>1, 3 and 5</c>. Null for a single episode (or none);
    /// <see cref="Season"/> / <see cref="Episode"/> are then the first one.
    /// </summary>
    public IReadOnlyList<EpisodeSpan>? Spans { get; init; }

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
        IReadOnlyList<EpisodeSpan>? spans = null;
        if (ParseSpans(ref rest) is { } multi)
        {
            spans = multi;
            (season, episode) = (multi[0].Season, multi[0].Episode);
        }

        foreach (var pattern in season is not null ? [] : (Regex[])[SxxEyy(), NxM(), SeasonEpisode(), EpisodeSeason()])
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
        return new PlayQuery(title, season, episode, start, quoted is not null, plain) { Spans = spans };
    }

    /// <summary>
    /// A start time word: a valid <see cref="MediaText.ParseTimestamp"/> time with a <c>:</c>/<c>.</c> separator or an
    /// <c>h</c>/<c>m</c>/<c>s</c> unit — a bare number is a title word (<c>space voyage 2049</c>).
    /// </summary>
    public static double? IsTimeWord(string word)
    {
        var marked = word.Contains(':', StringComparison.Ordinal) || word.Contains('.', StringComparison.Ordinal)
            || word.Any(ch => ch is 'h' or 'm' or 's' or 'H' or 'M' or 'S');
        return marked && !word.All(char.IsAsciiDigit) && word.Any(char.IsAsciiDigit) ? MediaText.ParseTimestamp(word) : null;
    }

    /// <summary>A human form of what was asked: <c>Example Show S02E03</c>, <c>Example Show season 2</c>.</summary>
    public string Describe()
    {
        var sb = new StringBuilder(Title);
        if (Spans is { } spans)
        {
            return $"{Title} {string.Join(", ", spans.Select(sp => sp.ToString()))}".Trim();
        }

        if (Season is { } s)
        {
            sb.Append(Episode is { } e
                ? string.Create(CultureInfo.InvariantCulture, $" S{s:00}E{e:00}")
                : string.Create(CultureInfo.InvariantCulture, $" season {s}"));
        }

        return sb.ToString().Trim();
    }

    /// <summary>A range or list of episodes (spec 018), cut out of <paramref name="rest"/>; null when there is none.</summary>
    private static IReadOnlyList<EpisodeSpan>? ParseSpans(ref string rest)
    {
        // s1e1-3 · s01e01-e03 · s1e1-s1e3 · s1e8-s2e2
        if (CodeRange().Match(rest) is { Success: true } code)
        {
            var s = Int(code.Groups[1].Value);
            var endSeason = code.Groups[3].Success ? Int(code.Groups[3].Value) : s;
            return Range(ref rest, code, s, Int(code.Groups[2].Value), endSeason, Int(code.Groups[4].Value));
        }

        // 1x1-3 · 1x8-2x2
        if (NxMRange().Match(rest) is { Success: true } nxm)
        {
            var s = Int(nxm.Groups[1].Value);
            var endSeason = nxm.Groups[3].Success ? Int(nxm.Groups[3].Value) : s;
            return Range(ref rest, nxm, s, Int(nxm.Groups[2].Value), endSeason, Int(nxm.Groups[4].Value));
        }

        // season 1 episode(s) 1-3 / 1 to 3
        if (WordsRange().Match(rest) is { Success: true } words)
        {
            var s = Int(words.Groups[1].Value);
            return Range(ref rest, words, s, Int(words.Groups[2].Value), s, Int(words.Groups[3].Value));
        }

        // episode(s) 1-3 season 1
        if (WordsRangeReversed().Match(rest) is { Success: true } reversed)
        {
            var s = Int(reversed.Groups[3].Value);
            return Range(ref rest, reversed, s, Int(reversed.Groups[1].Value), s, Int(reversed.Groups[2].Value));
        }

        // s1e1,3,5 · s1e1,e3
        if (CodeList().Match(rest) is { Success: true } codeList)
        {
            var s = Int(codeList.Groups[1].Value);
            return List(ref rest, codeList, s, [Int(codeList.Groups[2].Value), .. Numbers(codeList.Groups[3].Value)]);
        }

        // season 1 episodes 1, 3 and 5
        if (WordsList().Match(rest) is { Success: true } wordsList)
        {
            return List(ref rest, wordsList, Int(wordsList.Groups[1].Value), Numbers(wordsList.Groups[2].Value));
        }

        return null;
    }

    private static IReadOnlyList<EpisodeSpan>? Range(ref string rest, Match m, int season, int episode, int endSeason, int endEpisode)
    {
        if (endSeason < season || (endSeason == season && endEpisode < episode))
        {
            return null; // runs backwards: not a range (the words stay in the title)
        }

        rest = Cut(rest, m);
        return [new EpisodeSpan(season, episode, endSeason, endEpisode)];
    }

    private static IReadOnlyList<EpisodeSpan> List(ref string rest, Match m, int season, IReadOnlyList<int> episodes)
    {
        rest = Cut(rest, m);
        return [.. episodes.Distinct().Select(e => new EpisodeSpan(season, e, season, e))];
    }

    private static List<int> Numbers(string text) => [.. Digits().Matches(text).Select(d => Int(d.Value))];

    [GeneratedRegex(@"\bs([0-9]+)e([0-9]+)\s*-\s*(?:s([0-9]+)e|e)?([0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeRange();

    [GeneratedRegex(@"\b([0-9]+)x([0-9]+)\s*-\s*(?:([0-9]+)x)?([0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NxMRange();

    [GeneratedRegex(@"\bseason\s+([0-9]+)\s+episodes?\s+([0-9]+)\s*(?:-|to)\s*([0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordsRange();

    [GeneratedRegex(@"\bepisodes?\s+([0-9]+)\s*(?:-|to)\s*([0-9]+)\s+(?:of\s+)?season\s+([0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordsRangeReversed();

    [GeneratedRegex(@"\bs([0-9]+)e([0-9]+)((?:\s*,\s*e?[0-9]+)+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeList();

    [GeneratedRegex(@"\bseason\s+([0-9]+)\s+episodes?\s+([0-9]+(?:\s*(?:,|and|&)\s*[0-9]+)+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordsList();

    [GeneratedRegex("[0-9]+")]
    private static partial Regex Digits();

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

    [GeneratedRegex(@"\bseason\s+([0-9]+)\s+episodes?\s+([0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"\bepisodes?\s+([0-9]+)\s+(?:of\s+)?season\s+([0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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

/// <summary>Episodes from (<see cref="Season"/>, <see cref="Episode"/>) to (<see cref="EndSeason"/>, <see cref="EndEpisode"/>), inclusive.</summary>
public sealed record EpisodeSpan(int Season, int Episode, int EndSeason, int EndEpisode)
{
    public bool IsSingle => Season == EndSeason && Episode == EndEpisode;

    public override string ToString() => IsSingle
        ? Code(Season, Episode)
        : Season == EndSeason
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Code(Season, Episode)}–E{EndEpisode:00}")
            : $"{Code(Season, Episode)}–{Code(EndSeason, EndEpisode)}";

    internal static string Code(int season, int episode) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"S{season:00}E{episode:00}");
}

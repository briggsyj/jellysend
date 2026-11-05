using System.Globalization;
using JellySend.Services.Jellyfin;

namespace JellySend.Commands.Media;

/// <summary>
/// Finds the audio / subtitle option a casual argument names (spec 008): its number, <c>off</c> (subtitles), a
/// language code (<c>eng</c>, <c>en</c>) or a word of the track's label (<c>english</c>, <c>commentary</c>).
/// </summary>
public static class TrackMatch
{
    /// <summary>Two-letter → Jellyfin's three-letter (ISO 639-2) codes for the usual languages.</summary>
    private static readonly Dictionary<string, string> TwoLetter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng", ["ja"] = "jpn", ["jp"] = "jpn", ["fr"] = "fre", ["de"] = "ger", ["es"] = "spa", ["it"] = "ita",
        ["pt"] = "por", ["ru"] = "rus", ["zh"] = "chi", ["ko"] = "kor", ["nl"] = "dut", ["sv"] = "swe", ["no"] = "nor",
        ["da"] = "dan", ["fi"] = "fin", ["pl"] = "pol", ["tr"] = "tur", ["ar"] = "ara", ["hi"] = "hin", ["he"] = "heb",
        ["el"] = "gre", ["cs"] = "cze", ["hu"] = "hun", ["th"] = "tha", ["uk"] = "ukr", ["vi"] = "vie", ["id"] = "ind",
    };

    /// <summary>ISO 639-2 bibliographic ↔ terminology codes (Jellyfin may report either).</summary>
    private static readonly Dictionary<string, string> Alternate = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fre"] = "fra", ["fra"] = "fre", ["ger"] = "deu", ["deu"] = "ger", ["chi"] = "zho", ["zho"] = "chi",
        ["dut"] = "nld", ["nld"] = "dut", ["gre"] = "ell", ["ell"] = "gre", ["cze"] = "ces", ["ces"] = "cze",
    };

    public static bool IsOff(string text) => text.Trim().ToLowerInvariant() is "off" or "none" or "no" or "disable" or "hide";

    /// <summary>0-based index into <paramref name="options"/> (null = subtitles off), or null when nothing matches.</summary>
    public static int? Find(IReadOnlyList<MediaTrack?> options, string text, bool audio)
    {
        var t = text.Trim();
        if (t.Length == 0)
        {
            return null;
        }

        if (t.All(char.IsAsciiDigit))
        {
            return int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= options.Count ? n - 1 : null;
        }

        if (!audio && IsOff(t))
        {
            var off = options.ToList().FindIndex(o => o is null);
            return off >= 0 ? off : null;
        }

        var code = TwoLetter.TryGetValue(t, out var three) ? three : t;
        var alt = Alternate.GetValueOrDefault(code);
        var byCode = Best(options, o => string.Equals(o.Language, code, StringComparison.OrdinalIgnoreCase)
            || (alt is not null && string.Equals(o.Language, alt, StringComparison.OrdinalIgnoreCase)));
        return byCode ?? Best(options, o => o.Label.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The first match, preferring a full (not forced) track, then the default one.</summary>
    private static int? Best(IReadOnlyList<MediaTrack?> options, Func<MediaTrack, bool> matches)
    {
        var hits = options.Select((o, i) => (o, i)).Where(x => x.o is not null && matches(x.o)).ToList();
        if (hits.Count == 0)
        {
            return null;
        }

        return hits.OrderBy(x => x.o!.IsForced).ThenBy(x => !x.o!.IsDefault).ThenBy(x => x.i).First().i;
    }
}

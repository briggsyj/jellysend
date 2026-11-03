using Slopsoil.Commands.Media;

namespace Slopsoil.Tests.Commands;

public sealed class MediaTextTests
{
    // Cases from tests/test_jellyfin_commands.py.
    [Theory]
    [InlineData("1:23:45", 5025.0)]
    [InlineData("1.23.45", 5025.0)]
    [InlineData("1:23", 4980.0)]
    [InlineData("1.23", 4980.0)]
    [InlineData("0:05", 300.0)]
    [InlineData("0:00:30", 30.0)]
    [InlineData("0.0.7", 7.0)]
    [InlineData("2:5", 7500.0)]
    [InlineData("45", 2700.0)]
    [InlineData("0", 0.0)]
    [InlineData("120", 7200.0)]
    [InlineData("1:59:59", 7199.0)]
    [InlineData("1h30m", 5400.0)]
    [InlineData("45m", 2700.0)]
    [InlineData("90s", 90.0)]
    [InlineData("2h", 7200.0)]
    [InlineData("1h2m3s", 3723.0)]
    [InlineData("1h5s", 3605.0)]
    [InlineData("  1H30M  ", 5400.0)]
    [InlineData("1 h 30 m", 5400.0)]
    [InlineData(" 1:00 ", 3600.0)]
    [InlineData("90:00", 90 * 3600.0)]
    public void Parse_timestamp_valid(string text, double seconds) => Assert.Equal(seconds, MediaText.ParseTimestamp(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1:60")]
    [InlineData("1:23:60")]
    [InlineData("0:99")]
    [InlineData("1.75")]
    [InlineData("1:2:3:4")]
    [InlineData("1:234")]
    [InlineData(":30")]
    [InlineData("1:")]
    [InlineData("1::2")]
    [InlineData("1:-5")]
    [InlineData("-5")]
    [InlineData("30m1h")]
    [InlineData("1x")]
    [InlineData("h")]
    [InlineData("m")]
    [InlineData("1.5h")]
    [InlineData("tomorrow")]
    [InlineData("١٢")] // non-ASCII digits are not timestamps
    public void Parse_timestamp_invalid(string text) => Assert.Null(MediaText.ParseTimestamp(text));

    [Fact]
    public void Parse_timestamp_huge_numbers_do_not_overflow() =>
        Assert.True(MediaText.ParseTimestamp("99999999999999999999999") > 0);

    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(59.9, "0:00:59")]
    [InlineData(61, "0:01:01")]
    [InlineData(5025, "1:23:45")]
    [InlineData(-10, "0:00:00")]
    [InlineData(36000, "10:00:00")]
    public void Format_timestamp(double seconds, string text) => Assert.Equal(text, MediaText.FormatTimestamp(seconds));

    // Cases from tests/test_jellyfin.py.
    [Theory]
    [InlineData("inception", "inception", null, null)]
    [InlineData("rick and morty s02e01", "rick and morty", 2, 1)]
    [InlineData("Breaking Bad S03E10", "Breaking Bad", 3, 10)]
    [InlineData("The Office S01e03", "The Office", 1, 3)]
    [InlineData("  Arrested Development  s04e01  ", "Arrested Development", 4, 1)]
    [InlineData("s01e05", "", 1, 5)]
    [InlineData("show s12e24", "show", 12, 24)]
    [InlineData("show s01e01 s02e02", "show", 1, 1)]
    [InlineData("seas01ep01", "seas01ep01", null, null)]
    public void Parse_episode_query(string query, string show, int? season, int? episode) =>
        Assert.Equal((show, season, episode), MediaText.ParseEpisodeQuery(query));

    [Fact]
    public void Aspect_options_and_help_match_python()
    {
        Assert.Equal(
            ["Original (default) — keep the media's own shape", "16:9 — widescreen", "4:3 — classic TV", "2.39:1 — cinema scope (21:9)", "1.85:1 — theatrical widescreen"],
            MediaText.AspectOptions.Select(o => o.Label));
        Assert.Null(MediaText.AspectOptions[0].Ratio);
        Assert.Equal(2.39, MediaText.AspectOptions[3].Ratio);
        Assert.StartsWith("**🎬 Jellyfin — `!media`**\n\n**Play**", MediaText.HelpText, StringComparison.Ordinal);
        Assert.EndsWith("use `!media search <title>`.".Replace("use", "Use", StringComparison.Ordinal), MediaText.HelpText, StringComparison.Ordinal);
        Assert.Equal(10, MediaText.PlaylistMax);
    }
}

public sealed class SplitTrailingTimeTests
{
    [Theory]
    [InlineData("inception 1:12:30", "inception", 4350.0)]
    [InlineData("inception 1.12", "inception", 4320.0)]
    [InlineData("  the office s02e03 0:11  ", "the office s02e03", 660.0)]
    [InlineData("alien 45m", "alien", 2700.0)]
    [InlineData("alien 90s", "alien", 90.0)]
    [InlineData("apollo 13 1h2m3s", "apollo 13", 3723.0)]
    [InlineData("film 0:00", "film", 0.0)]
    [InlineData("film 1H30M", "film", 5400.0)]
    public void Splits_a_trailing_time(string query, string title, double seconds) =>
        Assert.Equal((title, (double?)seconds), MediaText.SplitTrailingTime(query));

    [Theory]
    [InlineData("inception")]
    [InlineData("blade runner 2049")]          // bare number: a title, never minutes here
    [InlineData("apollo 13")]
    [InlineData("1917")]
    [InlineData("2:22")]                       // time with no title in front: the title
    [InlineData("inception 1:75")]             // not a valid time
    [InlineData("the matrix reloaded")]        // ends in a word with h/m/s letters but not a time
    [InlineData("film 1.5h")]
    [InlineData("")]
    public void Leaves_everything_else_as_the_title(string query) =>
        Assert.Equal((query.Trim(), (double?)null), MediaText.SplitTrailingTime(query));
}

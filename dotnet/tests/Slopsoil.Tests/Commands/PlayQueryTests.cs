using Slopsoil.Commands.Media;

namespace Slopsoil.Tests.Commands;

/// <summary>Spec 008: casual <c>!play</c> arguments, recognised by format in any order.</summary>
public sealed class PlayQueryTests
{
    [Theory]
    [InlineData("inception", "inception", null, null, null)]
    [InlineData("rick and morty s02e01", "rick and morty", 2, 1, null)]
    [InlineData("Breaking Bad S03E10", "Breaking Bad", 3, 10, null)]
    [InlineData("The Office S01e03", "The Office", 1, 3, null)]
    [InlineData("  Arrested Development  s04e01  ", "Arrested Development", 4, 1, null)]
    [InlineData("s01e05", "", 1, 5, null)]
    [InlineData("show s12e24", "show", 12, 24, null)]
    [InlineData("show s01e01 s02e02", "show s02e02", 1, 1, null)]
    [InlineData("seas01ep01", "seas01ep01", null, null, null)]
    [InlineData("s1e1 the office", "the office", 1, 1, null)]             // any order
    [InlineData("the office 2x3", "the office", 2, 3, null)]
    [InlineData("the office 02X03", "the office", 2, 3, null)]
    [InlineData("the office season 2 episode 3", "the office", 2, 3, null)]
    [InlineData("the office Episode 3 Season 2", "the office", 2, 3, null)]
    [InlineData("the office s2", "the office", 2, null, null)]             // season only
    [InlineData("the office SEASON 02", "the office", 2, null, null)]
    [InlineData("the office episode 3", "the office episode 3", null, null, null)] // no season: a title
    [InlineData("season of the witch", "season of the witch", null, null, null)]
    [InlineData("inception 1:12:30", "inception", null, null, 4350.0)]
    [InlineData("inception 1.12", "inception", null, null, 4320.0)]
    [InlineData("1:12:30 inception", "inception", null, null, 4350.0)]
    [InlineData("the office s02e03 0:11", "the office", 2, 3, 660.0)]
    [InlineData("45m the office s2e3", "the office", 2, 3, 2700.0)]
    [InlineData("alien 90s", "alien", null, null, 90.0)]
    [InlineData("apollo 13 1h2m3s", "apollo 13", null, null, 3723.0)]
    [InlineData("film 1H30M", "film", null, null, 5400.0)]
    [InlineData("blade runner 2049", "blade runner 2049", null, null, null)] // bare number: a title word
    [InlineData("1917", "1917", null, null, null)]
    [InlineData("2:22", "", null, null, 8520.0)]
    [InlineData("inception 1:75", "inception 1:75", null, null, null)]       // not a valid time
    [InlineData("the matrix reloaded", "the matrix reloaded", null, null, null)]
    [InlineData("film 1.5h", "film 1.5h", null, null, null)]
    [InlineData("", "", null, null, null)]
    public void Recognises_formats(string text, string title, int? season, int? episode, double? start)
    {
        var q = PlayQuery.Parse(text);
        Assert.Equal((title, season, episode, start), (q.Title, q.Season, q.Episode, q.Start));
        Assert.False(q.Quoted);
    }

    [Theory]
    [InlineData("\"2:22\"", "2:22", null, null)]
    [InlineData("\"s1\" 45m", "s1", null, 2700.0)]
    [InlineData("“Season of the Witch” s01e02", "Season of the Witch", 1, null)]
    [InlineData("'9 to 5'", "9 to 5", null, null)]
    public void Quoted_text_is_always_title(string text, string title, int? season, double? start)
    {
        var q = PlayQuery.Parse(text);
        Assert.True(q.Quoted);
        Assert.Equal(title, q.Title);
        Assert.Equal(season, q.Season);
        Assert.Equal(start, q.Start);
    }

    [Fact]
    public void Apostrophes_inside_words_are_not_quotes()
    {
        var q = PlayQuery.Parse("ocean's eleven 1:00");
        Assert.Equal(("ocean's eleven", 3600.0), (q.Title, q.Start!.Value));
        Assert.False(q.Quoted);
    }

    [Fact]
    public void Queue_parsing_leaves_times_in_the_title()
    {
        var q = PlayQuery.Parse("inception 1:12", withTime: false);
        Assert.Equal(("inception 1:12", (double?)null), (q.Title, q.Start));
        Assert.Equal((2, 3), (PlayQuery.Parse("the office s2e3 1:00", withTime: false) is var e ? (e.Season!.Value, e.Episode!.Value) : default));
    }

    [Fact]
    public void Plain_title_is_the_whole_text()
    {
        var q = PlayQuery.Parse("the office s2e3 0:11");
        Assert.True(q.HasFormats);
        Assert.Equal(new PlayQuery("the office s2e3 0:11", null, null, null, false, "the office s2e3 0:11"), q.AsPlainTitle());
        Assert.False(PlayQuery.Parse("inception").HasFormats);
    }

    [Theory]
    [InlineData("the office s2e3", "the office S02E03")]
    [InlineData("the office s2", "the office season 2")]
    [InlineData("inception 1:00", "inception")]
    public void Describe_shows_what_was_asked(string text, string expected) => Assert.Equal(expected, PlayQuery.Parse(text).Describe());

    [Fact]
    public void Absurd_numbers_do_not_overflow() =>
        Assert.Equal(int.MaxValue, PlayQuery.Parse("show s99999999999e1").Season);
}

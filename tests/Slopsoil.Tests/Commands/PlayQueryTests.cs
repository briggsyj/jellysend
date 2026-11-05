using Slopsoil.Commands.Media;

namespace Slopsoil.Tests.Commands;

/// <summary>Spec 008: casual <c>!play</c> arguments, recognised by format in any order.</summary>
public sealed class PlayQueryTests
{
    [Theory]
    [InlineData("sample film", "sample film", null, null, null)]
    [InlineData("cartoon show s02e01", "cartoon show", 2, 1, null)]
    [InlineData("Drama Show S03E10", "Drama Show", 3, 10, null)]
    [InlineData("Example Show S01e03", "Example Show", 1, 3, null)]
    [InlineData("  Family Show  s04e01  ", "Family Show", 4, 1, null)]
    [InlineData("s01e05", "", 1, 5, null)]
    [InlineData("show s12e24", "show", 12, 24, null)]
    [InlineData("show s01e01 s02e02", "show s02e02", 1, 1, null)]
    [InlineData("seas01ep01", "seas01ep01", null, null, null)]
    [InlineData("s1e1 example show", "example show", 1, 1, null)]             // any order
    [InlineData("example show 2x3", "example show", 2, 3, null)]
    [InlineData("example show 02X03", "example show", 2, 3, null)]
    [InlineData("example show season 2 episode 3", "example show", 2, 3, null)]
    [InlineData("example show Episode 3 Season 2", "example show", 2, 3, null)]
    [InlineData("example show s2", "example show", 2, null, null)]             // season only
    [InlineData("example show SEASON 02", "example show", 2, null, null)]
    [InlineData("example show episode 3", "example show episode 3", null, null, null)] // no season: a title
    [InlineData("season of samples", "season of samples", null, null, null)]
    [InlineData("sample film 1:12:30", "sample film", null, null, 4350.0)]
    [InlineData("sample film 1.12", "sample film", null, null, 4320.0)]
    [InlineData("1:12:30 sample film", "sample film", null, null, 4350.0)]
    [InlineData("example show s02e03 0:11", "example show", 2, 3, 660.0)]
    [InlineData("45m example show s2e3", "example show", 2, 3, 2700.0)]
    [InlineData("creature 90s", "creature", null, null, 90.0)]
    [InlineData("mission 13 1h2m3s", "mission 13", null, null, 3723.0)]
    [InlineData("film 1H30M", "film", null, null, 5400.0)]
    [InlineData("space voyage 2049", "space voyage 2049", null, null, null)] // bare number: a title word
    [InlineData("1066", "1066", null, null, null)]
    [InlineData("3:33", "", null, null, 12780.0)]
    [InlineData("sample film 1:75", "sample film 1:75", null, null, null)]       // not a valid time
    [InlineData("the sample reloaded", "the sample reloaded", null, null, null)]
    [InlineData("film 1.5h", "film 1.5h", null, null, null)]
    [InlineData("", "", null, null, null)]
    public void Recognises_formats(string text, string title, int? season, int? episode, double? start)
    {
        var q = PlayQuery.Parse(text);
        Assert.Equal((title, season, episode, start), (q.Title, q.Season, q.Episode, q.Start));
        Assert.False(q.Quoted);
    }

    [Theory]
    [InlineData("\"3:33\"", "3:33", null, null)]
    [InlineData("\"s1\" 45m", "s1", null, 2700.0)]
    [InlineData("“Season of Samples” s01e02", "Season of Samples", 1, null)]
    [InlineData("'8 to 4'", "8 to 4", null, null)]
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
        var q = PlayQuery.Parse("sam's eleven 1:00");
        Assert.Equal(("sam's eleven", 3600.0), (q.Title, q.Start!.Value));
        Assert.False(q.Quoted);
    }

    [Fact]
    public void Queue_parsing_leaves_times_in_the_title()
    {
        var q = PlayQuery.Parse("sample film 1:12", withTime: false);
        Assert.Equal(("sample film 1:12", (double?)null), (q.Title, q.Start));
        Assert.Equal((2, 3), (PlayQuery.Parse("example show s2e3 1:00", withTime: false) is var e ? (e.Season!.Value, e.Episode!.Value) : default));
    }

    [Fact]
    public void Plain_title_is_the_whole_text()
    {
        var q = PlayQuery.Parse("example show s2e3 0:11");
        Assert.True(q.HasFormats);
        Assert.Equal(new PlayQuery("example show s2e3 0:11", null, null, null, false, "example show s2e3 0:11"), q.AsPlainTitle());
        Assert.False(PlayQuery.Parse("sample film").HasFormats);
    }

    [Theory]
    [InlineData("example show s2e3", "example show S02E03")]
    [InlineData("example show s2", "example show season 2")]
    [InlineData("sample film 1:00", "sample film")]
    public void Describe_shows_what_was_asked(string text, string expected) => Assert.Equal(expected, PlayQuery.Parse(text).Describe());

    [Fact]
    public void Absurd_numbers_do_not_overflow() =>
        Assert.Equal(int.MaxValue, PlayQuery.Parse("show s99999999999e1").Season);
}

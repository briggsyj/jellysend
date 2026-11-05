using Slopsoil.Commands.Media;
using Slopsoil.Services.Jellyfin;

namespace Slopsoil.Tests.Commands;

/// <summary>Spec 018: <c>!play &lt;show&gt; s1e1-3</c> and friends.</summary>
public sealed class MediaEpisodeRangeTests
{
    private readonly MediaHarness _h = new();

    public MediaEpisodeRangeTests()
    {
        _h.Library.Search["show"] = [new MediaItem { Id = "s1", Name = "Example Show", Type = "Series" }, MediaHarness.Movie("m", "Show", 2001)];
        _h.Library.Seasons["s1"] =
        [
            new MediaItem { Id = "se1", Name = "Season 1", IndexNumber = 1 },
            new MediaItem { Id = "se2", Name = "Season 2", IndexNumber = 2 },
        ];
        _h.Library.Episodes["se1"] = [.. Enumerable.Range(1, 10).Select(n => MediaHarness.Episode($"a{n}", "Example Show", 1, n, $"One{n}"))];
        _h.Library.Episodes["se2"] = [.. Enumerable.Range(1, 4).Select(n => MediaHarness.Episode($"b{n}", "Example Show", 2, n, $"Two{n}"))];
    }

    private static string L(int s, int e) => $"Example Show — S{s:00}E{e:00} — {(s == 1 ? "One" : "Two")}{e}";

    private async Task<string[]> Playlist()
    {
        return [.. (await _h.ShowPlaylist()).Split('\n').Skip(2).Select(l => l.Trim())];
    }

    [Fact]
    public async Task Play_a_range_plays_the_first_and_queues_the_rest_next()
    {
        await _h.Send("!play show season 1 episode 1-3");
        Assert.Equal(L(1, 1), _h.Current.Request.Title);
        Assert.Equal(["`1` " + L(1, 2), "`2` " + L(1, 3)], await Playlist());
    }

    [Fact]
    public async Task A_range_across_seasons_keeps_season_order()
    {
        await _h.Send("!play show s1e9-s2e2");
        Assert.Equal(L(1, 9), _h.Current.Request.Title);
        Assert.Equal(["`1` " + L(1, 10), "`2` " + L(2, 1), "`3` " + L(2, 2)], await Playlist());
    }

    [Fact]
    public async Task Missing_episodes_are_reported_and_the_rest_play()
    {
        await _h.Send("!play show s1e8-12");
        Assert.Contains("🔎 S01E11–E12 of **Example Show** isn't in the library — carrying on with the rest.", _h.Texts);
        Assert.Equal(L(1, 8), _h.Current.Request.Title);
        Assert.Equal(["`1` " + L(1, 9), "`2` " + L(1, 10)], await Playlist());
    }

    [Fact]
    public async Task Missing_seasons_are_reported()
    {
        await _h.Send("!play show s2e3-s4e1");
        Assert.Contains("🔎 season 4 of **Example Show** isn't in the library — carrying on with the rest.", _h.Texts);
        Assert.Equal(L(2, 3), _h.Current.Request.Title);
    }

    [Fact]
    public async Task Nothing_found_at_all_plays_nothing()
    {
        await _h.Send("!play show s5e1-3");
        Assert.Equal(["🔎 S05E01–E03 of **Example Show** isn't in the library."], _h.Texts);
        Assert.Empty(_h.Pipelines.Created);
        _h.Sender.Sent.Clear();
        await _h.Send("!play nothing s1e1-3");
        Assert.Equal(["🔎 S01E01–E03 of `nothing` isn't in the library."], _h.Texts);
    }

    [Fact]
    public async Task Queue_appends_a_list_in_order()
    {
        _h.Library.Search["show 2x4"] = [MediaHarness.Episode("b4", "Example Show", 2, 4, "Two4")];
        await _h.Send("!play show s2e4");
        await _h.Send("!playlist add show s1e5,2,7");
        Assert.Equal(["`1` " + L(1, 5), "`2` " + L(1, 2), "`3` " + L(1, 7)], await Playlist());
    }

    [Fact]
    public void Missing_text_joins_consecutive_episodes()
    {
        Assert.Equal("S01E11–E12, S01E14, season 3",
            MediaModule.MissingText([(1, 11), (1, 12), (1, 14), (3, null), (3, 2)]));
    }
}

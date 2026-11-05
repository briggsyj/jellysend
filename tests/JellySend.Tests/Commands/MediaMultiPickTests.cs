using JellySend.Commands.Interaction;
using JellySend.Commands.Media;
using JellySend.Services.Jellyfin;

namespace JellySend.Tests.Commands;

/// <summary>Spec 014: several episodes from one picker reply (<c>2,5,8</c>, <c>3-6</c>).</summary>
public sealed class MediaMultiPickTests
{
    private const string Prompt = "📺 **Example Show — Season 1**. Reply with an episode, or several in order (`2,5,8`, `3-6`):";

    private readonly MediaHarness _h = new();

    public MediaMultiPickTests()
    {
        _h.Library.Search["example"] = [new MediaItem { Id = "s1", Name = "Example Show", Type = "Series" }];
        _h.Library.Seasons["s1"] = [new MediaItem { Id = "se1", Name = "Season 1", IndexNumber = 1 }];
        _h.Library.Episodes["se1"] = [.. Enumerable.Range(1, 6).Select(n => MediaHarness.Episode($"e{n}", "Example Show", 1, n, $"Ep{n}"))];
        _h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000)];
    }

    private static string Label(int n) => $"Example Show — S01E{n:00} — Ep{n}";


    [Fact]
    public async Task Play_starts_the_first_pick_and_queues_the_rest_next_in_order()
    {
        await _h.Send("!playlist add film");                 // idle: plays now
        await _h.Send("!playlist add film");                 // queued #1
        var run = _h.Start("!play example");
        await _h.Until(() => _h.Texts.Any(t => t.StartsWith(Prompt, StringComparison.Ordinal)), "the episode picker");
        await _h.Answer("5, 2,4");
        await run;
        Assert.Equal(Label(5), _h.Current.Request.Title);
        Assert.Equal($"➕ Added to the playlist up next: **{Label(2)}**, **{Label(4)}**", _h.Texts[^1]);
        Assert.Equal(["`1` " + Label(2), "`2` " + Label(4), "`3` Film (2000)"], (await _h.ShowPlaylist()).Split('\n').Skip(2).Select(l => l.Trim()));
    }

    [Fact]
    public async Task Queue_appends_every_pick_in_order_including_ranges()
    {
        await _h.Send("!play film");
        var run = _h.Start("!playlist add example");
        await _h.Until(() => _h.Texts.Any(t => t.StartsWith(Prompt, StringComparison.Ordinal)), "the episode picker");
        await _h.Answer("2-4,1");
        await run;
        Assert.Equal($"➕ Added to the playlist from #1: **{Label(2)}**, **{Label(3)}**, **{Label(4)}**, **{Label(1)}**", _h.Texts[^1]);
    }

    [Fact]
    public async Task Idle_queue_plays_the_first_pick_and_queues_the_rest()
    {
        var run = _h.Start("!playlist add example");
        await _h.Until(() => _h.Texts.Any(t => t.StartsWith(Prompt, StringComparison.Ordinal)), "the episode picker");
        await _h.Answer("3,6");
        await run;
        Assert.Equal(Label(3), _h.Current.Request.Title);
        Assert.Equal($"➕ Added to the playlist up next: **{Label(6)}**", _h.Texts[^1]);
    }

    [Fact]
    public async Task Picks_that_do_not_fit_the_playlist_are_reported()
    {
        await _h.Send("!play film");
        for (var i = 0; i < MediaText.PlaylistMax - 2; i++)
        {
            await _h.Send("!playlist add film");
        }

        var run = _h.Start("!playlist add example");
        await _h.Until(() => _h.Texts.Any(t => t.StartsWith(Prompt, StringComparison.Ordinal)), "the episode picker");
        await _h.Answer("1-4");
        await run;
        Assert.Equal(
            $"➕ Added to the playlist from #{MediaText.PlaylistMax - 1}: **{Label(1)}**, **{Label(2)}**\n🙅 The playlist is full ({MediaText.PlaylistMax} items) — 2 not added.",
            _h.Texts[^1]);
    }

    [Fact]
    public async Task A_bad_list_cancels_with_a_hint()
    {
        var run = _h.Start("!play example");
        await _h.Until(() => _h.Texts.Any(t => t.StartsWith(Prompt, StringComparison.Ordinal)), "the episode picker");
        await _h.Answer("2,9");
        await run;
        Assert.Equal("🙅 That's not on the list — pick numbers between 1 and 6 (e.g. `2`, `2,5,8` or `3-6`).", _h.Texts[^1]);
        Assert.Empty(_h.Pipelines.Created);
    }

    [Theory]
    [InlineData("2", new[] { 2 })]
    [InlineData("2,5,8", new[] { 2, 5, 8 })]
    [InlineData(" 8 , 2,5 ", new[] { 8, 2, 5 })]
    [InlineData("3-6", new[] { 3, 4, 5, 6 })]
    [InlineData("1, 3 - 5, 9", new[] { 1, 3, 4, 5, 9 })]
    [InlineData("2,2,3-4,3", new[] { 2, 3, 4 })]
    [InlineData("4-4", new[] { 4 })]
    public void Parse_list(string text, int[] picks) => Assert.Equal(picks, NumberPrompt.ParseList(text, 9));

    [Theory]
    [InlineData("0")]
    [InlineData("10")]
    [InlineData("2,10")]
    [InlineData("5-3")]
    [InlineData("2,,3")]
    [InlineData("2;3")]
    [InlineData("two")]
    [InlineData("")]
    [InlineData("-3")]
    public void Parse_list_rejects(string text) => Assert.Null(NumberPrompt.ParseList(text, 9));

    [Fact]
    public void Insert_next_keeps_order_and_the_cap()
    {
        var p = new Playlist<string>(s => s);
        p.Add("x");
        Assert.Equal(2, p.InsertNext(["a", "b"]));
        Assert.Equal(["a", "b", "x"], p.Snapshot());
        for (var i = p.Count; i < MediaText.PlaylistMax - 1; i++)
        {
            p.Add("y");
        }

        Assert.Equal(1, p.InsertNext(["c", "d"]));
        Assert.Equal("c", p.Snapshot()[0]);
        Assert.Equal(0, p.InsertNext(["e"]));
    }
}

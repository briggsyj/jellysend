using Slopsoil.Services.Jellyfin;

namespace Slopsoil.Tests.Commands;

/// <summary>Spec 002: <c>!media &lt;title&gt; &lt;time&gt;</c>.</summary>
public sealed class MediaStartAtTests
{
    private readonly MediaHarness _h = new();

    public MediaStartAtTests()
    {
        _h.Library.Search["sample film"] = [MediaHarness.Movie("i", "Sample Film", 2010, (2L * 3600 + 28 * 60) * 10_000_000)];
        _h.Library.Search["space voyage 2049"] = [MediaHarness.Movie("b", "Space Voyage 2049", 2017)];
        _h.Library.Search["3:33"] = [MediaHarness.Movie("t", "3:33", 2017)];
    }

    [Theory]
    [InlineData("!play sample film 1:12:30", 4350.0, "⏩ starting at 1:12:30")]
    [InlineData("!play sample film 1.12", 4320.0, "⏩ starting at 1:12:00")]
    [InlineData("!play sample film 45m", 2700.0, "⏩ starting at 0:45:00")]
    public async Task Starts_directly_at_the_time(string command, double offset, string reply)
    {
        await _h.Send(command);
        Assert.Equal(offset, _h.Current.Request.StartOffset);
        Assert.Equal([reply, "checking stream…", "📺 **Sample Film (2010)** (go-live)"], _h.Texts);
    }

    [Fact]
    public async Task Zero_time_plays_from_the_beginning_without_a_start_reply()
    {
        await _h.Send("!play sample film 0:00");
        Assert.Equal(0.0, _h.Current.Request.StartOffset);
        Assert.Equal("checking stream…", _h.Texts[0]);
    }

    [Fact]
    public async Task Picker_and_episode_code_and_seasons_keep_the_time()
    {
        _h.Library.Search["creature"] = [MediaHarness.Movie("a1", "Creature", 1979), MediaHarness.Movie("a2", "Creatures", 1986)];
        var run = _h.Start("!play creature 0:45");
        await _h.Answer("2");
        await run;
        Assert.Equal(("Creatures (1986)", 2700.0), (_h.Current.Request.Title, _h.Current.Request.StartOffset));

        _h.Library.Search["example show 2x3"] = [MediaHarness.Episode("e", "Example Show", 2, 3, "Field Day")];
        await _h.Send("!play example show s02e03 0:11");
        Assert.Equal(("Example Show — S02E03 — Field Day", 660.0), (_h.Current.Request.Title, _h.Current.Request.StartOffset));

        _h.Library.Search["show"] = [new MediaItem { Id = "s", Name = "Show", Type = "Series" }];
        _h.Library.Seasons["s"] = [new MediaItem { Id = "se", Name = "Season 1" }];
        _h.Library.Episodes["se"] = [MediaHarness.Episode("ep", "Show", 1, 1, "Pilot")];
        var series = _h.Start("!play show 5m");
        await _h.Answer("1");
        await series;
        Assert.Equal(("Show — S01E01 — Pilot", 300.0), (_h.Current.Request.Title, _h.Current.Request.StartOffset));
    }

    [Theory]
    [InlineData("!play space voyage 2049", "Space Voyage 2049 (2017)")]
    [InlineData("!play 3:33", "3:33 (2017)")]
    public async Task Titles_ending_in_numbers_play_from_the_beginning(string command, string title)
    {
        await _h.Send(command);
        Assert.Equal((title, 0.0), (_h.Current.Request.Title, _h.Current.Request.StartOffset));
        Assert.DoesNotContain(_h.Texts, t => t.StartsWith("⏩", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Full_title_is_tried_when_the_title_without_the_time_finds_nothing()
    {
        _h.Library.Search["live at 9.30"] = [MediaHarness.Movie("l", "Live at 9.30", 2005)];
        await _h.Send("!play live at 9.30");
        Assert.Equal(("Live at 9.30 (2005)", 0.0), (_h.Current.Request.Title, _h.Current.Request.StartOffset));
        Assert.DoesNotContain(_h.Texts, t => t.StartsWith("nothing found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_found_reports_the_full_text_once()
    {
        await _h.Send("!play zzz 1:00");
        Assert.Equal(["nothing found in Jellyfin for `zzz 1:00`"], _h.Texts);
    }

    [Fact]
    public async Task Past_the_end_is_rejected_and_nothing_starts()
    {
        await _h.Send("!play sample film 9:00:00");
        await _h.Send("!play sample film 2:28");
        Assert.Equal(["9:00:00 is past the end (2:28:00)", "2:28:00 is past the end (2:28:00)"], _h.Texts);
        Assert.Empty(_h.Pipelines.Created);
    }

    [Fact]
    public async Task Picker_cancel_sends_no_start_message()
    {
        _h.Library.Search["creature"] = [MediaHarness.Movie("a1", "Creature"), MediaHarness.Movie("a2", "Creatures")];
        var run = _h.Start("!play creature 0:45");
        await _h.Answer("!ping");
        await run;
        Assert.DoesNotContain(_h.Texts, t => t.StartsWith("⏩", StringComparison.Ordinal));
        Assert.Empty(_h.Pipelines.Created);
    }

    [Fact]
    public async Task Queue_does_not_take_a_start_time()
    {
        _h.Library.Search["sample film 1:12"] = [MediaHarness.Movie("q", "Sample Film 1:12 Cut", 2011)];
        await _h.Send("!media queue sample film 1:12");
        Assert.Equal(("Sample Film 1:12 Cut (2011)", 0.0), (_h.Current.Request.Title, _h.Current.Request.StartOffset));
    }

    [Fact]
    public async Task Help_documents_the_start_time_form()
    {
        await _h.Send("!media help");
        Assert.Contains("`!play <title> 1:20:00` — start partway through", _h.Texts[0], StringComparison.Ordinal);
    }
}

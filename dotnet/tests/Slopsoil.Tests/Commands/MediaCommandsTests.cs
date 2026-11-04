using Slopsoil.Commands.Media;
using Slopsoil.Services.Jellyfin;

namespace Slopsoil.Tests.Commands;

/// <summary>Spec 008: every command as <c>!&lt;name&gt;</c> and <c>!media &lt;name&gt;</c>, per-command help, casual arguments.</summary>
public sealed class MediaCommandsTests
{
    private readonly MediaHarness _h = new();

    private static readonly MediaItem Office = new() { Id = "s1", Name = "The Office", Type = "Series" };

    public MediaCommandsTests()
    {
        _h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000)];
        _h.Library.Search["office"] = [Office];
        _h.Library.Seasons["s1"] =
        [
            new MediaItem { Id = "se1", Name = "Season 1", IndexNumber = 1 },
            new MediaItem { Id = "se2", Name = "Season 2", IndexNumber = 2 },
        ];
        _h.Library.Episodes["se2"] = [MediaHarness.Episode("e1", "The Office", 2, 1, "The Dundies"), MediaHarness.Episode("e2", "The Office", 2, 2, "Sexual Harassment")];
        _h.Library.Search["office 2x1"] = [MediaHarness.Episode("e1", "The Office", 2, 1, "The Dundies")];
    }

    // ── registration and help ────────────────────────────────────────────

    [Fact]
    public void Every_catalog_command_has_a_help_and_unique_names()
    {
        var names = CommandCatalog.All.SelectMany(c => c.Names).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("resume", names);
        Assert.DoesNotContain("search", names);
        Assert.All(CommandCatalog.All, c => Assert.StartsWith($"**`!{c.Name}`**", c.HelpText(), StringComparison.Ordinal));
        Assert.Equal(["order", "queue", "remove"], CommandCatalog.All.Where(c => c.NeedsArgs).Select(c => c.Name).Order());
    }

    [Theory]
    [InlineData("play", "play")]
    [InlineData("!ar", "aspect-ratio")]
    [InlineData("media subs", "subtitles")]
    [InlineData("!media SUB", "subtitles")]
    [InlineData("  Time ", "time")]
    public void Catalog_finds_commands_by_any_name(string text, string name) => Assert.Equal(name, CommandCatalog.Find(text)?.Name);

    [Theory]
    [InlineData("")]
    [InlineData("media")]
    [InlineData("resume")]
    [InlineData("nope")]
    public void Catalog_misses_unknown_names(string text) => Assert.Null(CommandCatalog.Find(text));

    [Fact]
    public void Help_text_shows_aliases_and_the_media_form()
    {
        var ar = CommandCatalog.Find("ar")!.HelpText();
        Assert.StartsWith("**`!aspect-ratio`** (also `!aspect`, `!ar`)\n`!ar` — list", ar, StringComparison.Ordinal);
        Assert.EndsWith("-# also `!media aspect-ratio`", ar, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("!help play", "play")]
    [InlineData("!help !subs", "subtitles")]
    [InlineData("!media help media ar", "aspect-ratio")]
    [InlineData("!HELP Queue", "queue")]
    public async Task Help_for_one_command(string content, string name)
    {
        await _h.Send(content);
        Assert.Equal([CommandCatalog.Find(name)!.HelpText()], _h.Texts);
    }

    [Theory]
    [InlineData("!queue", "queue")]
    [InlineData("!media queue", "queue")]
    [InlineData("!order", "order")]
    [InlineData("!media remove", "remove")]
    public async Task Bare_commands_that_need_arguments_show_their_help(string content, string name)
    {
        await _h.Send(content);
        Assert.Equal([CommandCatalog.Find(name)!.HelpText()], _h.Texts);
    }

    [Theory]
    [InlineData("!pause", "!media pause")]
    [InlineData("!skip", "!media skip")]
    [InlineData("!time", "!media time")]
    [InlineData("!playlist", "!media playlist")]
    [InlineData("!debug", "!media debug")]
    [InlineData("!ar 2", "!media aspect-ratio 2")]
    [InlineData("!aspect 2", "!media ar 2")]
    [InlineData("!subs", "!media subtitles")]
    [InlineData("!sub off", "!media subs off")]
    [InlineData("!audio", "!media audio")]
    [InlineData("!remove 1", "!media remove 1")]
    [InlineData("!order 1 2", "!media order 1 2")]
    [InlineData("!Play", "!media play")]
    public async Task Shortcuts_and_media_forms_reply_alike(string shortcut, string full)
    {
        await _h.Send(full);
        var viaFull = _h.Texts.ToList();
        _h.Sender.Sent.Clear();
        await _h.Send(shortcut);
        Assert.Equal(viaFull, _h.Texts);
        Assert.NotEmpty(viaFull);
    }

    [Fact]
    public async Task Bare_media_shows_help_without_a_hint()
    {
        await _h.Send("!media");
        Assert.Equal([MediaText.HelpText], _h.Texts);
    }

    // ── !play formats ────────────────────────────────────────────────────

    [Theory]
    [InlineData("!play office 2x1")]
    [InlineData("!play office S02E01")]
    [InlineData("!play s2e1 office")]
    [InlineData("!play office season 2 episode 1")]
    [InlineData("!play office episode 1 season 2")]
    public async Task Episode_formats_find_the_episode(string content)
    {
        await _h.Send(content);
        Assert.Equal("The Office — S02E01 — The Dundies", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Episode_with_a_start_time()
    {
        await _h.Send("!play office s02e01 10m");
        Assert.Equal(600, _h.Current.Request.StartOffset);
        Assert.Contains("⏩ starting at 0:10:00", _h.Texts);
    }

    [Theory]
    [InlineData("!play office s2")]
    [InlineData("!play office Season 02")]
    public async Task Season_only_skips_the_season_picker(string content)
    {
        var run = _h.Start(content);
        await _h.UntilReply("**The Office — Season 2**. Pick an episode:\n\n  `1` E01 — The Dundies\n  `2` E02 — Sexual Harassment");
        await _h.Answer("2");
        await run;
        Assert.Equal("The Office — S02E02 — Sexual Harassment", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Missing_season_says_so_and_offers_the_seasons()
    {
        var run = _h.Start("!play office s9");
        await _h.UntilReply("**The Office** has no season 9. **The Office** — 2 seasons. Pick a season:\n\n  `1` Season 1\n  `2` Season 2");
        await _h.Answer("2");
        await _h.UntilReply("**The Office — Season 2**. Pick an episode:\n\n  `1` E01 — The Dundies\n  `2` E02 — Sexual Harassment");
        await _h.Answer("1");
        await run;
        Assert.Equal("The Office — S02E01 — The Dundies", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Season_only_prefers_shows_over_same_named_films()
    {
        _h.Library.Search["office"] = [MediaHarness.Movie("m", "Office", 2015), Office];
        var run = _h.Start("!play office s2");
        await _h.UntilReply("**The Office — Season 2**. Pick an episode:\n\n  `1` E01 — The Dundies\n  `2` E02 — Sexual Harassment");
        await _h.Answer("1");
        await run;
        Assert.Equal("The Office — S02E01 — The Dundies", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Formats_that_find_nothing_fall_back_to_the_whole_text()
    {
        _h.Library.Search["the 4:30 movie"] = [MediaHarness.Movie("m", "The 4:30 Movie", 2024)];
        await _h.Send("!play the 4:30 movie");
        Assert.Equal("The 4:30 Movie (2024)", _h.Current.Request.Title);
        Assert.Equal(0, _h.Current.Request.StartOffset); // the time was part of the title
        Assert.DoesNotContain(_h.Texts, t => t.StartsWith("nothing found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_formats_search_the_whole_text()
    {
        _h.Library.Search["s01e02"] = [MediaHarness.Movie("x", "S01E02", 2020)];
        await _h.Send("!play s01e02");
        Assert.Equal("S01E02 (2020)", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Misses_report_what_was_asked()
    {
        await _h.Send("!play nothing 2x3");
        await _h.Send("!play nothing s4");
        await _h.Send("!play nothing 1:00");
        Assert.Equal(
            ["S02E03 of `nothing` not found in Jellyfin", "nothing found in Jellyfin for `nothing s4`", "nothing found in Jellyfin for `nothing 1:00`"],
            _h.Texts);
    }

    [Fact]
    public async Task Quoted_titles_never_fall_back()
    {
        _h.Library.Search["film 1:00"] = [MediaHarness.Movie("w", "Wrong", 2000)];
        await _h.Send("!play \"film\" 0:10");
        Assert.Equal("Film (2000)", _h.Current.Request.Title);
        Assert.Equal(600, _h.Current.Request.StartOffset);
        _h.Sender.Sent.Clear();
        await _h.Send("!play \"gone\" 0:10");
        Assert.Equal(["nothing found in Jellyfin for `gone 0:10`"], _h.Texts);
    }

    [Fact]
    public async Task Queue_takes_episode_formats_but_never_a_start_time()
    {
        await _h.Send("!play film");
        _h.Library.Search["film 1:00"] = [MediaHarness.Movie("w", "Film One", 2001)];
        await _h.Send("!queue office 2x1");
        await _h.Send("!queue film 1:00");
        Assert.Equal(["➕ queued **The Office — S02E01 — The Dundies** at #1", "➕ queued **Film One (2001)** at #2"], _h.Texts.TakeLast(2));
    }

    // ── casual names for pickers ─────────────────────────────────────────

    [Theory]
    [InlineData("widescreen", "16:9")]
    [InlineData("16:9", "16:9")]
    [InlineData("TV", "4:3")]
    [InlineData("21:9", "2.39:1")]
    [InlineData("theatrical", "1.85:1")]
    public async Task Aspect_by_name(string arg, string shown)
    {
        await _h.Send("!ar 2");
        await _h.Send($"!ar {arg}");
        Assert.Equal($"📐 aspect ratio set to **{shown}**", _h.Texts[^1]);
    }

    [Theory]
    [InlineData("original", 0)]
    [InlineData("Default", 0)]
    [InlineData("1", 0)]
    [InlineData("5", 4)]
    [InlineData("1.78", 1)]
    [InlineData("cinemascope", 3)]
    [InlineData("6", null)]
    [InlineData("0", null)]
    [InlineData("rahman", null)]
    [InlineData(" ", null)]
    public void Find_aspect(string text, int? index) => Assert.Equal(index, MediaText.FindAspect(text));
}

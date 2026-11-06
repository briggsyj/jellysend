using JellySend.Commands.Routing;
using JellySend.Commands.Media;
using JellySend.Services.Jellyfin;

namespace JellySend.Tests.Commands;

/// <summary>Spec 008: every command as <c>!&lt;name&gt;</c> and <c>!media &lt;name&gt;</c>, per-command help, casual arguments.</summary>
public sealed class MediaCommandsTests
{
    private readonly MediaHarness _h = new();

    private static readonly MediaItem Example = new() { Id = "s1", Name = "Example Show", Type = "Series" };

    public MediaCommandsTests()
    {
        _h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000)];
        _h.Library.Search["example"] = [Example];
        _h.Library.Seasons["s1"] =
        [
            new MediaItem { Id = "se1", Name = "Season 1", IndexNumber = 1 },
            new MediaItem { Id = "se2", Name = "Season 2", IndexNumber = 2 },
        ];
        _h.Library.Episodes["se2"] = [MediaHarness.Episode("e1", "Example Show", 2, 1, "Awards Night"), MediaHarness.Episode("e2", "Example Show", 2, 2, "Second Chances")];
        _h.Library.Search["example 2x1"] = [MediaHarness.Episode("e1", "Example Show", 2, 1, "Awards Night")];
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
        Assert.StartsWith("**`!aspect-ratio`** (also `!aspect`, `!ar`)\nAspect ratio — the picture shape", ar, StringComparison.Ordinal);
        Assert.EndsWith("-# Also works as `!media aspect-ratio`.", ar, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("!help play", "play")]
    [InlineData("!help !subs", "subtitles")]
    [InlineData("!media help media ar", "aspect-ratio")]
    [InlineData("!HELP Playlist", "playlist")]
    public async Task Help_for_one_command(string content, string name)
    {
        await _h.Send(content);
        Assert.Equal([CommandCatalog.Find(name)!.HelpText()], _h.Texts);
    }

    [Theory]
    [InlineData("!queue", "queue", "add", "➕ **What should we add?**")]
    [InlineData("!media order 1 2", "order", "order", "📭 The playlist is empty.")]
    [InlineData("!remove 1", "remove", "remove", "📭 The playlist is empty.")]
    public async Task Old_playlist_commands_point_to_their_new_name(string content, string old, string action, string then)
    {
        _ = _h.Start(content);
        await _h.Until(() => _h.Texts.Count == 2, "two replies");
        Assert.Equal($"-# `!{old}` is now `!playlist {action}`.", _h.Texts[0]);
        Assert.StartsWith(then, _h.Texts[1], StringComparison.Ordinal);
        Assert.Equal("playlist", CommandCatalog.Find(content)?.Name); // its help is !playlist's
    }

    [Theory]
    [InlineData("!pause", "!media pause")]
    [InlineData("!skip", "!media skip")]
    [InlineData("!time", "!media time")]
    [InlineData("!debug", "!media debug")]
    [InlineData("!ar 2", "!media aspect-ratio 2")]
    [InlineData("!aspect 2", "!media ar 2")]
    [InlineData("!subs", "!media subtitles")]
    [InlineData("!sub off", "!media subs off")]
    [InlineData("!audio", "!media audio")]
    [InlineData("!playlist remove 1", "!media playlist remove 1")]
    [InlineData("!playlist order 1 2", "!media playlist order 1 2")]
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
    [InlineData("!play example 2x1")]
    [InlineData("!play example S02E01")]
    [InlineData("!play s2e1 example")]
    [InlineData("!play example season 2 episode 1")]
    [InlineData("!play example episode 1 season 2")]
    public async Task Episode_formats_find_the_episode(string content)
    {
        await _h.Send(content);
        Assert.Equal("Example Show — S02E01 — Awards Night", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Episode_with_a_start_time()
    {
        await _h.Send("!play example s02e01 10m");
        Assert.Equal(600, _h.Current.Request.StartOffset);
        Assert.Contains("⏩ Starting at 0:10:00", _h.Texts);
    }

    [Theory]
    [InlineData("!play example s2")]
    [InlineData("!play example Season 02")]
    public async Task Season_only_skips_the_season_picker(string content)
    {
        var run = _h.Start(content);
        await _h.UntilReply("📺 **Example Show — Season 2**. Reply with an episode, or several in order (`2,5,8`, `3-6`):\n\n  `1` E01 — Awards Night\n  `2` E02 — Second Chances");
        await _h.Answer("2");
        await run;
        Assert.Equal("Example Show — S02E02 — Second Chances", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Missing_season_says_so_and_offers_the_seasons()
    {
        var run = _h.Start("!play example s9");
        await _h.UntilReply("🔎 **Example Show** has no season 9. 📺 **Example Show** — 2 seasons. Reply with a season:\n\n  `1` Season 1\n  `2` Season 2");
        await _h.Answer("2");
        await _h.UntilReply("📺 **Example Show — Season 2**. Reply with an episode, or several in order (`2,5,8`, `3-6`):\n\n  `1` E01 — Awards Night\n  `2` E02 — Second Chances");
        await _h.Answer("1");
        await run;
        Assert.Equal("Example Show — S02E01 — Awards Night", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Season_only_prefers_shows_over_same_named_films()
    {
        _h.Library.Search["example"] = [MediaHarness.Movie("m", "Example", 2015), Example];
        var run = _h.Start("!play example s2");
        await _h.UntilReply("📺 **Example Show — Season 2**. Reply with an episode, or several in order (`2,5,8`, `3-6`):\n\n  `1` E01 — Awards Night\n  `2` E02 — Second Chances");
        await _h.Answer("1");
        await run;
        Assert.Equal("Example Show — S02E01 — Awards Night", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Formats_that_find_nothing_fall_back_to_the_whole_text()
    {
        _h.Library.Search["the 4:30 sample"] = [MediaHarness.Movie("m", "The 4:30 Sample", 2024)];
        await _h.Send("!play the 4:30 sample");
        Assert.Equal("The 4:30 Sample (2024)", _h.Current.Request.Title);
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
            ["🔎 S02E03 of `nothing` isn't in the library.", "🔎 Nothing found for `nothing s4` — check the spelling?", "🔎 Nothing found for `nothing 1:00` — check the spelling?"],
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
        Assert.Equal(["🔎 Nothing found for `gone 0:10` — check the spelling?"], _h.Texts);
    }

    [Fact]
    public async Task Queue_takes_episode_formats_but_never_a_start_time()
    {
        await _h.Send("!play film");
        _h.Library.Search["film 1:00"] = [MediaHarness.Movie("w", "Film One", 2001)];
        await _h.Send("!playlist add example 2x1");
        await _h.Send("!playlist add film 1:00");
        Assert.Equal(["➕ Added to the playlist: **Example Show — S02E01 — Awards Night** (#1)", "➕ Added to the playlist: **Film One (2001)** (#2)"], _h.Texts.TakeLast(2));
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
        Assert.Equal($"📐 Aspect ratio set to **{shown}**.", _h.Texts[^1]);
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
    [InlineData("sample", null)]
    [InlineData(" ", null)]
    public void Find_aspect(string text, int? index) => Assert.Equal(index, MediaText.FindAspect(text));
}

/// <summary>Spec 010: <c>!resolution</c> / <c>!res</c>.</summary>
public sealed class MediaResolutionTests
{
    private readonly MediaHarness _h = new();

    public MediaResolutionTests() => _h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000)];

    [Theory]
    [InlineData("360p", 1)]
    [InlineData("480", 2)]
    [InlineData("540P", 3)]
    [InlineData("1280x720", 4)]
    [InlineData("1920:1080", 5)]
    [InlineData("hd", 4)]
    [InlineData("Full HD", 5)]
    [InlineData("sd", 2)]
    [InlineData("default", 0)]
    [InlineData("1", 0)]
    [InlineData("6", 5)]
    [InlineData("7", null)]
    [InlineData("1440p", null)]
    [InlineData("big", null)]
    [InlineData("", null)]
    public void Find_resolution(string text, int? index) => Assert.Equal(index, MediaText.FindResolution(text));

    [Fact]
    public async Task Idle_choice_applies_to_the_next_item()
    {
        await _h.Send("!res 480p");
        Assert.Equal(["📺 Resolution set to **480p** — from the next programme."], _h.Texts);
        await _h.Send("!play film");
        Assert.Equal(480, _h.Current.Request.OutputHeight);
        await _h.Send("!media resolution 480");
        Assert.Equal("📺 Resolution set to **480p**.", _h.Texts[^1]);
    }

    [Fact]
    public async Task Playing_choice_restarts_at_the_position_and_default_clears_it()
    {
        await _h.Send("!play film");
        Assert.Null(_h.Current.Request.OutputHeight);
        _h.Current.Fake.Position = 125;
        _h.Sender.Sent.Clear();
        await _h.Send("!res 720p");
        Assert.Equal("📺 Resolution set to **720p** — adjusting the picture…", _h.Texts[0]);
        Assert.Equal(2, _h.Pipelines.Created.Count);
        Assert.Equal((720, 125.0), (_h.Current.Request.OutputHeight!.Value, _h.Current.Request.StartOffset));
        await _h.Send("!res default");
        Assert.Null(_h.Current.Request.OutputHeight);
        Assert.Equal(3, _h.Pipelines.Created.Count);
    }

    [Fact]
    public async Task Bare_shows_the_picker_which_takes_names()
    {
        await _h.Send("!res 540p");
        var run = _h.Start("!res");
        await _h.UntilReply("📺 **Resolution** — reply with a number or name:\n`1` Default — the configured resolution\n`2` 360p\n`3` 480p\n`4` 540p  ← current\n`5` 720p\n`6` 1080p");
        await _h.Answer("360p");
        await run;
        Assert.Equal("📺 Resolution set to **360p** — from the next programme.", _h.Texts[^1]);
    }

    [Fact]
    public async Task Unknown_words_show_the_picker_and_bad_numbers_say_so()
    {
        await _h.Send("!res 9");
        Assert.Equal(["🙅 Pick a number between 1 and 6 — see `!res`."], _h.Texts);
        var run = _h.Start("!res huge");
        await _h.UntilReply("🙅 `huge` isn't a resolution. 📺 **Resolution** — reply with a number or name:\n`1` Default — the configured resolution  ← current\n`2` 360p\n`3` 480p\n`4` 540p\n`5` 720p\n`6` 1080p");
        await _h.Answer("1");
        await run;
        Assert.Equal("📺 Resolution set to **Default**.", _h.Texts[^1]);
    }

    [Fact]
    public async Task Needs_a_guild()
    {
        _h.Gateway.States.Clear();
        await _h.Send("!res 480p", guild: null);
        Assert.Equal(["🙅 Join a voice channel first, then try again."], _h.Texts);
    }

    [Fact]
    public async Task Viewers_get_the_denied_gif()
    {
        await _h.Send("!res 480p", author: 30);
        Assert.Equal([CommandRouter.DeniedReply], _h.Texts);
    }
}

/// <summary>Spec 019: <c>!help all</c> and <c>!language</c>.</summary>
public sealed class MediaHelpAllAndLanguageTests
{
    private readonly MediaHarness _h = new();

    public MediaHelpAllAndLanguageTests()
    {
        _h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000)];
        _h.Library.Tracks["f"] =
        [
            new(1, true, "eng", "English 5.1", true, false, EmbeddedOrdinal: 0),
            new(2, true, "jpn", "Japanese", false, false, EmbeddedOrdinal: 1),
            new(3, false, "eng", "English", false, false, EmbeddedOrdinal: 0),
            new(4, false, "fre", "French", false, false, EmbeddedOrdinal: 1),
        ];
    }

    [Fact]
    public async Task Help_all_documents_every_command_in_messages_under_the_limit()
    {
        await _h.Send("!help all");
        Assert.Equal(CommandCatalog.FullGuide(), _h.Texts);
        Assert.All(_h.Texts, t => Assert.True(t.Length <= 2000, $"{t.Length} characters"));
        var all = string.Join("\n", _h.Texts);
        Assert.All(CommandCatalog.All, c => Assert.Contains($"**`!{c.Name}`**", all, StringComparison.Ordinal));
        Assert.StartsWith("## 📺 JellySend — the full guide\n### ▶️ Watch", _h.Texts[0], StringComparison.Ordinal);
        Assert.Equal(CommandCatalog.Sections.Count + 1, _h.Texts.Count(t => t.Contains("### ", StringComparison.Ordinal)));
        Assert.StartsWith(CommandCatalog.EventsHelp, _h.Texts[^1], StringComparison.Ordinal); // spec 028, then the footer
    }

    [Theory]
    [InlineData("!help events")]
    [InlineData("!help Event")]
    [InlineData("!help screenings")]
    [InlineData("!help !events")]
    public async Task Help_events_explains_the_screenings_format(string content)
    {
        await _h.Send(content);
        Assert.Equal([CommandCatalog.EventsHelp], _h.Texts);
        Assert.True(CommandCatalog.EventsHelp.Length <= CommandCatalog.MessageLimit);
        Assert.Contains("Screenings:\n1. Big Buck Bunny", CommandCatalog.EventsHelp, StringComparison.Ordinal);
        Assert.Contains("`!help events`", MediaText.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_example_in_the_events_help_parses()
    {
        var example = CommandCatalog.EventsHelp.Split("```")[1];
        Assert.Equal(["Big Buck Bunny", "Example Show s1e1-3"], JellySend.Commands.Events.Screenings.Parse(example));
    }

    [Fact]
    public void A_section_too_long_for_one_message_continues()
    {
        Assert.All(CommandCatalog.FullGuide(), m => Assert.True(m.Length <= CommandCatalog.MessageLimit + 200));
        Assert.Contains(CommandCatalog.All, c => c.Section == CommandCatalog.PictureAndSound && c.Name == "language");
        Assert.Equal("language", CommandCatalog.Find("lang")!.Name);
    }

    [Fact]
    public async Task Language_sets_audio_and_subtitles_in_one_switch()
    {
        await _h.Send("!play film");
        _h.Sender.Sent.Clear();
        await _h.Send("!language japanese english");
        Assert.Equal("🎧 Audio: **Japanese** · 💬 Subtitles: **English** — switching over…", _h.Texts[0]);
        Assert.Equal(new TrackSelection(2, 3), _h.Library.PlaybackCalls[^1].Tracks);
        Assert.Equal(2, _h.Pipelines.Created.Count); // one restart for both
    }

    [Fact]
    public async Task Language_variants()
    {
        await _h.Send("!play film");
        await _h.Send("!lang jpn");                     // audio only
        Assert.Equal(new TrackSelection(2, null), _h.Library.PlaybackCalls[^1].Tracks);
        await _h.Send("!media language english fr");    // both
        Assert.Equal(new TrackSelection(1, 4), _h.Library.PlaybackCalls[^1].Tracks);
        await _h.Send("!language english off");         // subtitles off
        Assert.Equal(new TrackSelection(1, null), _h.Library.PlaybackCalls[^1].Tracks);
        var calls = _h.Library.PlaybackCalls.Count;
        await _h.Send("!language english off");         // unchanged
        Assert.Equal("🎧 Audio: **English 5.1** · 💬 Subtitles: **off** — already on.", _h.Texts[^1]);
        Assert.Equal(calls, _h.Library.PlaybackCalls.Count);
        await _h.Send("!language");
        Assert.StartsWith("🎧 Audio: **English 5.1** · 💬 Subtitles: **off**\n-# `!language <audio> <subtitles>`", _h.Texts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Language_errors_list_what_the_item_has()
    {
        await _h.Send("!language japanese english");     // nothing playing: its help
        Assert.Equal("🙅 Nothing's on air right now.", _h.Texts[^1]);
        await _h.Send("!language");
        Assert.Equal(CommandCatalog.Find("language")!.HelpText(), _h.Texts[^1]);
        await _h.Send("!play film");
        await _h.Send("!language klingon");
        Assert.Equal("🙅 No audio track matches `klingon` — this one has: English 5.1, Japanese.", _h.Texts[^1]);
        await _h.Send("!language english klingon");
        Assert.Equal("🙅 No subtitles match `klingon` — this one has: English, French (or `off`).", _h.Texts[^1]);
        await _h.Send("!language a b c");
        Assert.Equal(CommandCatalog.Find("language")!.HelpText(), _h.Texts[^1]);
    }
}

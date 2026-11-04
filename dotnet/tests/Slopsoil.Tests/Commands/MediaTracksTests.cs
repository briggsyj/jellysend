using Slopsoil.Services.Jellyfin;

namespace Slopsoil.Tests.Commands;

/// <summary>Spec 005: <c>!media subtitles</c> / <c>!media audio</c>.</summary>
public sealed class MediaTracksTests
{
    private readonly MediaHarness _h = new();

    private static readonly List<MediaTrack> FilmTracks =
    [
        new(1, true, "eng", "English 5.1", true, false),
        new(2, true, "jpn", "Japanese", false, false),
        new(3, false, "eng", "English - SRT", false, false),
        new(4, false, "fre", "French", false, false),
        new(5, false, "eng", "English signs", false, true),
    ];

    public MediaTracksTests()
    {
        _h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000)];
        _h.Library.Search["next"] = [MediaHarness.Movie("n", "Next", 2001)];
        _h.Library.Tracks["f"] = FilmTracks;
        _h.Library.Tracks["n"] =
        [
            new(10, true, "eng", "English", true, false),
            new(11, true, "jpn", "Japanese", false, false),
            new(12, false, "eng", "English signs", false, true),
            new(13, false, "eng", "English", false, false),
        ];
    }

    private async Task PlayFilm()
    {
        await _h.Send("!play film");
        _h.Current.Fake.Position = 600;
        _h.Sender.Sent.Clear();
    }

    [Fact]
    public async Task Subtitle_picker_lists_off_and_tracks_and_switches_in_place()
    {
        await PlayFilm();
        var run = _h.Start("!media subtitles");
        await _h.UntilReply("**Subtitles** — reply with a number or language:\n`1` Off  ← current\n`2` English - SRT\n`3` French\n`4` English signs (forced)");
        await _h.Answer("2");
        await run;
        Assert.Contains("💬 subtitles: **English - SRT** — switching…", _h.Texts);
        Assert.Equal(("f", new TrackSelection(null, 3)), _h.Library.PlaybackCalls[^1]);
        Assert.Equal(("Film (2000)", 600.0), (_h.Current.Request.Title, _h.Current.Request.StartOffset)); // same item, same place
        Assert.Equal(2, _h.Pipelines.Created.Count);
    }

    [Fact]
    public async Task Direct_numbers_off_and_aliases()
    {
        await PlayFilm();
        await _h.Send("!media subs 3");
        Assert.Equal(new TrackSelection(null, 4), _h.Library.PlaybackCalls[^1].Tracks);
        await _h.Send("!media sub off");
        Assert.Equal(new TrackSelection(null, null), _h.Library.PlaybackCalls[^1].Tracks);
        await _h.Send("!media subs off");                    // already off
        Assert.Equal("💬 subtitles: off (already on)", _h.Texts[^1]);
        await _h.Send("!media subs 9");
        Assert.Equal("pick a number between 1 and 4 — see `!subs`", _h.Texts[^1]);
    }

    [Fact]
    public async Task Audio_picker_marks_the_default_and_switches()
    {
        await PlayFilm();
        var run = _h.Start("!media audio");
        await _h.UntilReply("**Audio track** — reply with a number or language:\n`1` English 5.1  ← current\n`2` Japanese");
        await _h.Answer("2");
        await run;
        Assert.Contains("🔊 audio: **Japanese** — switching…", _h.Texts);
        Assert.Equal(new TrackSelection(2, null), _h.Library.PlaybackCalls[^1].Tracks);
        await _h.Send("!media audio 2");
        Assert.Equal("🔊 audio: **Japanese** (already on)", _h.Texts[^1]);
    }

    [Fact]
    public async Task Choices_are_remembered_by_language_for_the_next_item()
    {
        await PlayFilm();
        await _h.Send("!media subs 2");    // English subtitles
        await _h.Send("!media audio 2");   // Japanese audio
        await _h.Send("!play next");
        // First the default session, then reopened with the remembered languages; forced English signs are skipped.
        Assert.Equal([("n", null), ("n", new TrackSelection(11, 13))], _h.Library.PlaybackCalls.TakeLast(2));
        var defaultSession = $"ps{_h.Library.PlaybackCalls.Count - 1}";  // the fake numbers sessions by call
        Assert.Contains(defaultSession, _h.Library.Stopped);              // the default session was released
        Assert.Equal("Next (2001)", _h.Current.Request.Title);
    }

    [Fact]
    public async Task No_match_falls_back_to_defaults_and_leave_forgets()
    {
        await PlayFilm();
        await _h.Send("!media subs 3");                       // French subtitles
        _h.Library.Search["other"] = [MediaHarness.Movie("o", "Other", 2002)];
        _h.Library.Tracks["o"] = [new(1, true, "eng", "English", true, false), new(2, false, "eng", "English", false, false)];
        await _h.Send("!play other");
        Assert.Equal(("o", null), _h.Library.PlaybackCalls[^1]); // no French track: default session kept

        await _h.Send("!media subs 2");
        await _h.Send("!media leave");
        await _h.Send("!play other");
        Assert.Equal(("o", null), _h.Library.PlaybackCalls[^1]); // forgotten on leave
    }

    [Fact]
    public async Task Nothing_playing_and_no_subtitles()
    {
        await _h.Send("!media subs");
        Assert.Equal(["nothing is playing"], _h.Texts);
        _h.Library.Search["bare"] = [MediaHarness.Movie("b", "Bare", 1999)];
        await _h.Send("!play bare");
        _h.Sender.Sent.Clear();
        await _h.Send("!media subtitles");
        Assert.Equal(["this item has no subtitles"], _h.Texts);
        await _h.Send("!media audio");
        Assert.Equal("this item has no audio tracks to choose from", _h.Texts[^1]);
        await _h.Send("!subs off");
        Assert.Equal("💬 subtitles: off (already on)", _h.Texts[^1]);
    }

    [Fact]
    public async Task Help_lists_the_track_commands()
    {
        await _h.Send("!media help");
        Assert.Contains("`!subs` — subtitles (`!subs english`, `!subs off`) · `!audio` — audio track (`!audio japanese`)", _h.Texts[0], StringComparison.Ordinal);
    }
}

/// <summary>Spec 008: <c>!subs english</c>, <c>!audio japanese</c>, and names as picker replies.</summary>
public sealed class MediaTrackNamesTests
{
    private readonly MediaHarness _h = new();

    public MediaTrackNamesTests()
    {
        _h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000)];
        _h.Library.Tracks["f"] =
        [
            new(1, true, "eng", "English 5.1", true, false),
            new(2, true, "jpn", "Japanese", false, false),
            new(3, false, "eng", "English - SRT", false, false),
            new(4, false, "fre", "French", false, false),
        ];
    }

    [Fact]
    public async Task Tracks_by_language_name_or_code()
    {
        await _h.Send("!play film");
        await _h.Send("!subs english");
        Assert.Equal(new TrackSelection(null, 3), _h.Library.PlaybackCalls[^1].Tracks);
        await _h.Send("!audio jpn");
        Assert.Equal(new TrackSelection(2, 3), _h.Library.PlaybackCalls[^1].Tracks);
        await _h.Send("!sub fr");
        Assert.Equal(new TrackSelection(2, 4), _h.Library.PlaybackCalls[^1].Tracks);
    }

    [Fact]
    public async Task Unknown_names_show_the_picker_which_takes_names_too()
    {
        await _h.Send("!play film");
        _h.Sender.Sent.Clear();
        var run = _h.Start("!audio klingon");
        await _h.UntilReply("no audio track match `klingon`. **Audio track** — reply with a number or language:\n`1` English 5.1  ← current\n`2` Japanese");
        await _h.Answer("japanese");
        await run;
        Assert.Equal(new TrackSelection(2, null), _h.Library.PlaybackCalls[^1].Tracks);
    }

    [Fact]
    public async Task Picker_ignores_chat_that_names_nothing()
    {
        await _h.Send("!play film");
        var run = _h.Start("!subs");
        await _h.UntilReply("**Subtitles** — reply with a number or language:\n`1` Off  ← current\n`2` English - SRT\n`3` French");
        await _h.Answer("lol which one");
        await _h.Answer("french");
        await run;
        Assert.Equal(new TrackSelection(null, 4), _h.Library.PlaybackCalls[^1].Tracks);
    }
}

using Slopsoil.Commands.Media;
using Slopsoil.Commands.Routing;
using Slopsoil.Services.Jellyfin;
using Slopsoil.Services.Media;
using Slopsoil.Streaming.Engine;

namespace Slopsoil.Tests.Commands;

public sealed class MediaPlayTests
{
    private readonly MediaHarness _h = new();

    [Fact]
    public async Task Single_match_plays_in_the_authors_channel()
    {
        _h.Library.Search["sample film"] = [MediaHarness.Movie("m1", "Sample Film")];
        await _h.Send("!play sample film");

        Assert.Equal(["📡 Tuning in to **Sample Film (2010)**…", "📺 Now showing: **Sample Film (2010)**"], _h.Texts);
        var p = _h.Current;
        Assert.Equal("Sample Film (2010)", p.Request.Title);
        Assert.Equal("http://jf/videos/m1/variant.m3u8?api_key=SECRET", p.Request.Url);
        Assert.True(p.Request.HasAudio);
        Assert.Equal(10_000_000, p.Request.ProbeSize);
        Assert.Equal((5UL, 50UL), (_h.Gateway.Links[0].GuildId, _h.Gateway.Links[0].ChannelId));
        Assert.True(_h.Media.IsStreaming(5));
    }

    [Fact]
    public async Task Bare_media_and_help_send_the_help_text()
    {
        await _h.Send("!media");
        await _h.Send("!media help");
        Assert.Equal([MediaText.HelpText, MediaText.HelpText], _h.Texts);
    }

    [Fact]
    public async Task Help_mentions_the_syncplay_group_when_a_login_is_configured()
    {
        _h.Library.HasUserCredentials = true;
        await _h.Send("!media help");
        await _h.Send("!media help", guild: 9, author: 30); // a viewer in a server whose name is unknown
        Assert.EndsWith("join **slopsoil · Movie Night** in a Jellyfin app to pause, play and seek this stream together.", _h.Texts[0], StringComparison.Ordinal);
        Assert.EndsWith("join **slopsoil · <server name>** in a Jellyfin app to pause, play and seek this stream together.", _h.Texts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unconfigured_library_says_so_but_help_still_works()
    {
        var h = new MediaHarness(configured: false);
        await h.Send("!play sample film");
        await h.Send("!media playlist add sample film");
        await h.Send("!media");
        Assert.StartsWith("❌ The Jellyfin library isn't set up.", h.Texts[0], StringComparison.Ordinal);
        Assert.StartsWith("❌ The Jellyfin library isn't set up.", h.Texts[1], StringComparison.Ordinal);
        Assert.Equal(MediaText.HelpText, h.Texts[2]);
    }

    [Fact]
    public async Task Picker_lists_results_and_plays_the_choice()
    {
        _h.Library.Search["creature"] = [MediaHarness.Movie("a1", "Creature", 1979), MediaHarness.Movie("a2", "Creatures", 1986)];
        var run = _h.Start("!play creature");
        await _h.UntilReply("🔎 Found 2 matches for `creature` — reply with a number:\n\n  `1` [Movie] Creature (1979)\n  `2` [Movie] Creatures (1986)");
        await _h.Answer("2");
        await run;
        Assert.Equal("Creatures (1986)", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Picker_timeout_cancels()
    {
        _h.Library.Search["creature"] = [MediaHarness.Movie("a1", "Creature"), MediaHarness.Movie("a2", "Creatures")];
        var run = _h.Start("!play creature");
        await _h.Until(() => _h.Texts.Count == 1);
        // The picker may not be waiting yet: keep advancing the fake clock until it gives up.
        while (!run.IsCompleted)
        {
            _h.Time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await run;
        Assert.Equal("⌛ No answer — cancelled.", _h.Texts[^1]);
        Assert.Empty(_h.Pipelines.Created);
    }

    [Fact]
    public async Task Series_walks_season_then_episode()
    {
        _h.Library.Search["example"] = [new MediaItem { Id = "s1", Name = "Example Show", Type = "Series" }];
        _h.Library.Seasons["s1"] = [new MediaItem { Id = "se1", Name = "Season 1" }, new MediaItem { Id = "se2", Name = "Season 2" }];
        _h.Library.Episodes["se2"] = [MediaHarness.Episode("e1", "Example Show", 2, 1, "Awards Night"), new MediaItem { Id = "e2", Name = "Special", Type = "Episode", SeriesName = "Example Show" }];
        var run = _h.Start("!play example");
        await _h.UntilReply("📺 **Example Show** — 2 seasons. Reply with a season:\n\n  `1` Season 1\n  `2` Season 2");
        await _h.Answer("2");
        await _h.UntilReply("📺 **Example Show — Season 2**. Reply with an episode, or several in order (`2,5,8`, `3-6`):\n\n  `1` E01 — Awards Night\n  `2` Special");
        await _h.Answer("1");
        await run;
        Assert.Equal("Example Show — S02E01 — Awards Night", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Single_season_skips_the_season_picker_and_labels_without_codes()
    {
        _h.Library.Search["show"] = [new MediaItem { Id = "s1", Type = "Series" }];
        _h.Library.Seasons["s1"] = [new MediaItem { Id = "se1" }];
        _h.Library.Episodes["se1"] = [new MediaItem { Id = "e1", Name = "Pilot", Type = "Episode", SeriesName = "Show" }];
        var run = _h.Start("!play show");
        await _h.UntilReply("📺 **Unknown — Unknown**. Reply with an episode:\n\n  `1` Pilot");
        await _h.Answer("1");
        await run;
        Assert.Equal("Show — Pilot", _h.Current.Request.Title);
    }

    [Theory]
    [InlineData("show s01e02", "🔎 S01E02 of `show` isn't in the library.")]
    [InlineData("zzz", "🔎 Nothing found for `zzz` — check the spelling?")]
    public async Task Nothing_found(string query, string reply)
    {
        await _h.Send($"!play {query}");
        Assert.Equal([reply], _h.Texts);
    }

    [Fact]
    public async Task Episode_code_finds_the_episode_directly()
    {
        _h.Library.Search["example show 2x3"] = [MediaHarness.Episode("e", "Example Show", 2, 3, "Field Day")];
        await _h.Send("!play Example Show S02E03");
        Assert.Equal("Example Show — S02E03 — Field Day", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Search_errors_distinguish_unreachable()
    {
        _h.Library.SearchError = new JellyfinException("Jellyfin request failed", new HttpRequestException("connection refused"));
        await _h.Send("!play x");
        _h.Library.SearchError = new JellyfinException("Jellyfin /Items → HTTP 500");
        await _h.Send("!play x");
        _h.Library.SearchError = new JellyfinException("bad JSON");
        await _h.Send("!play x");
        Assert.Equal(
            ["❌ We can't reach the Jellyfin library right now. Try again shortly — if it keeps happening, ask an admin.", "❌ We can't reach the Jellyfin library right now. Try again shortly — if it keeps happening, ask an admin.", "❌ The Jellyfin search didn't work. Try again — if it keeps happening, ask an admin to check the logs."],
            _h.Texts); // Python: HTTPError is a URLError → "could not reach"
    }

    [Fact]
    public async Task Season_and_episode_fetch_errors_and_empties()
    {
        _h.Library.Search["bad"] = [new MediaItem { Id = "bad", Name = "Bad", Type = "Series" }];
        _h.Library.Search["empty"] = [new MediaItem { Id = "empty", Name = "Empty", Type = "Series" }];
        _h.Library.Search["eps"] = [new MediaItem { Id = "eps", Name = "Eps", Type = "Series" }];
        _h.Library.Seasons["eps"] = [new MediaItem { Id = "bad", Name = "S1" }];
        _h.Library.Search["noeps"] = [new MediaItem { Id = "noeps", Name = "NoEps", Type = "Series" }];
        _h.Library.Seasons["noeps"] = [new MediaItem { Id = "x", Name = "S1" }];
        await _h.Send("!play bad");
        await _h.Send("!play empty");
        await _h.Send("!play eps");
        await _h.Send("!play noeps");
        Assert.Equal(
            ["❌ We couldn't load the seasons of **Bad** from Jellyfin. Try again in a moment.", "🔎 No seasons of **Empty** in the library.", "❌ We couldn't load the episodes from Jellyfin. Try again in a moment.", "🔎 No episodes of **NoEps** S1 in the library."],
            _h.Texts);
    }

    [Fact]
    public async Task Not_in_voice()
    {
        _h.Gateway.States.Clear();
        _h.Library.Search["sample film"] = [MediaHarness.Movie("m1", "Sample Film")];
        await _h.Send("!play sample film");
        Assert.Equal(["🙅 Join a voice channel first, then try again."], _h.Texts);
    }

    [Fact]
    public async Task Playback_failures_reply_and_release_the_transcode()
    {
        _h.Library.Search["x"] = [MediaHarness.Movie("m1", "X", null)];
        _h.Library.NoPlayback = true;
        await _h.Send("!play x");
        _h.Library.NoPlayback = false;

        _h.Library.UnknownMetadata = true;
        await _h.Send("!play x");                       // probe fails
        _h.Library.Probe = new ProbeResult("vp9", 24, "p", false, 1, 1, false, null);
        await _h.Send("!play x");                       // probed codec unsupported
        _h.Library.UnknownMetadata = false;
        _h.Library.Codec = "av1";
        await _h.Send("!play x");                       // PlaybackInfo codec unsupported

        Assert.Equal(
            [
                "📡 Tuning in to **X**…", "❌ Jellyfin wouldn't play **X**. Ask an admin to check the server and the bot's Jellyfin login.",
                "📡 Tuning in to **X**…", "❌ Jellyfin couldn't prepare **X**. Try again — if it keeps happening, ask an admin to check the Jellyfin logs.",
                "📡 Tuning in to **X**…", "❌ **X** uses a video format (`vp9`) we can't show.",
                "📡 Tuning in to **X**…", "❌ **X** uses a video format (`av1`) we can't show.",
            ],
            _h.Texts);
        Assert.Equal(["ps1", "ps2", "ps3"], _h.Library.Stopped);
    }

    [Fact]
    public async Task Probe_supplies_audio_flag_when_metadata_is_missing()
    {
        _h.Library.Search["x"] = [MediaHarness.Movie("m1", "X")];
        _h.Library.UnknownMetadata = true;
        _h.Library.Probe = new ProbeResult("hevc", 24, "main", false, 1920, 1080, false, null);
        await _h.Send("!play x");
        Assert.False(_h.Current.Request.HasAudio);
    }

    [Fact]
    public async Task Go_live_failure_releases_the_transcode_and_clears_now_playing()
    {
        _h.Library.Search["x"] = [MediaHarness.Movie("m1", "X")];
        _h.Connector.Next.Enqueue(_ => Task.FromException<IGoLiveLink>(new TimeoutException("slow")));
        await _h.Send("!play x");
        Assert.Equal("❌ We couldn't open the screenshare. Try again in a moment — if it keeps happening, ask an admin to check the logs.", _h.Texts[^1]);
        Assert.Equal(["ps1"], _h.Library.Stopped);
        Assert.Equal("📭 Nothing on air and nothing on the playlist.", await _h.ShowPlaylist("!media playlist"));
    }

    [Fact]
    public async Task Replacing_an_item_releases_the_previous_transcode()
    {
        _h.Library.Search["a"] = [MediaHarness.Movie("a", "A")];
        _h.Library.Search["b"] = [MediaHarness.Movie("b", "B")];
        await _h.Send("!play a");
        await _h.Send("!play b");
        Assert.Equal(["ps1"], _h.Library.Stopped);
        Assert.Equal(1, _h.Pipelines.Created[0].Stops);
    }

    [Fact]
    public async Task Old_title_and_search_forms_point_to_play()
    {
        await _h.Send("!media stop-making-sense");
        await _h.Send("!media search sample film");
        Assert.StartsWith("-# `!media <title>` is now `!play <title>` — e.g. `!play stop-making-sense`\n\n### 📺 Slopsoil — now broadcasting", _h.Texts[0], StringComparison.Ordinal);
        Assert.StartsWith("-# `!media search` is gone: use `!play <title>`\n\n### 📺 Slopsoil — now broadcasting", _h.Texts[1], StringComparison.Ordinal);
        Assert.Empty(_h.Pipelines.Created);
        _h.Library.Search["queue"] = [MediaHarness.Movie("q", "Queue")];
        await _h.Send("!play queue");
        Assert.Equal("Queue (2010)", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Voice_join_failure_is_reported()
    {
        var h = new MediaHarness();
        h.Library.Search["x"] = [MediaHarness.Movie("m1", "X")];
        var failing = new FailingGateway(h.Gateway);
        var voice = new Slopsoil.Commands.Voice.VoiceManager(failing);
        var media = new MediaModule(h.Library, h.Engine, voice);
        var router = new CommandRouter(new FakeRoles(), h.Sender, new MessageHub());
        media.Register(router, new Slopsoil.Commands.Voice.VoiceModule(voice, media));
        await router.DispatchAsync(new ChatMessage(5, 7, 20, "!play x"), TestContext.Current.CancellationToken);
        Assert.Equal("❌ We couldn't join your voice channel. Check the bot can see and join it, then try again.", h.Texts[^1]);
        Assert.Equal(["ps1"], h.Library.Stopped);
    }

    private sealed class FailingGateway(FakeVoiceGateway inner) : Slopsoil.Commands.Voice.IVoiceGateway
    {
        public ulong SelfId => inner.SelfId;

        public ulong? GetVoiceChannel(ulong guildId, ulong userId) => inner.GetVoiceChannel(guildId, userId);

        public (ulong Guild, ulong Channel)? FindUserVoice(ulong userId) => inner.FindUserVoice(userId);

        public IReadOnlyCollection<ulong> UsersInChannel(ulong guildId, ulong channelId) => inner.UsersInChannel(guildId, channelId);

        public Task<Slopsoil.Commands.Voice.IVoiceLink> ConnectAsync(ulong guildId, ulong channelId, CancellationToken ct) =>
            Task.FromException<Slopsoil.Commands.Voice.IVoiceLink>(new InvalidOperationException("no permission"));
    }
}

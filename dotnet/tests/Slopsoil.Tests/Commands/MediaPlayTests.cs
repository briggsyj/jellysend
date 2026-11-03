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
        _h.Library.Search["inception"] = [MediaHarness.Movie("m1", "Inception")];
        await _h.Send("!media inception");

        Assert.Equal(["checking stream…", "📺 **Inception (2010)** (go-live)"], _h.Texts);
        var p = _h.Current;
        Assert.Equal("Inception (2010)", p.Request.Title);
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
        await _h.Send("!media help", guild: 9, author: 30 + 0); // friend 20 only — this one is silent
        Assert.Single(_h.Texts);
        Assert.EndsWith("join **slopsoil · Movie Night** in a Jellyfin app to pause, play and seek this stream together.", _h.Texts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unconfigured_library_says_so_but_help_still_works()
    {
        var h = new MediaHarness(configured: false);
        await h.Send("!media inception");
        await h.Send("!media queue inception");
        await h.Send("!media");
        Assert.StartsWith("Jellyfin is not configured.", h.Texts[0], StringComparison.Ordinal);
        Assert.StartsWith("Jellyfin is not configured.", h.Texts[1], StringComparison.Ordinal);
        Assert.Equal(MediaText.HelpText, h.Texts[2]);
    }

    [Fact]
    public async Task Picker_lists_results_and_plays_the_choice()
    {
        _h.Library.Search["alien"] = [MediaHarness.Movie("a1", "Alien", 1979), MediaHarness.Movie("a2", "Aliens", 1986)];
        var run = _h.Start("!media alien");
        await _h.UntilReply("Found 2 result(s) for `alien`. Pick a number:\n\n  `1` [Movie] Alien (1979)\n  `2` [Movie] Aliens (1986)");
        await _h.Answer("2");
        await run;
        Assert.Equal("Aliens (1986)", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Picker_timeout_cancels()
    {
        _h.Library.Search["alien"] = [MediaHarness.Movie("a1", "Alien"), MediaHarness.Movie("a2", "Aliens")];
        var run = _h.Start("!media alien");
        await _h.Until(() => _h.Texts.Count == 1);
        // The picker may not be waiting yet: keep advancing the fake clock until it gives up.
        while (!run.IsCompleted)
        {
            _h.Time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await run;
        Assert.Equal("timed out — cancelled", _h.Texts[^1]);
        Assert.Empty(_h.Pipelines.Created);
    }

    [Fact]
    public async Task Series_walks_season_then_episode()
    {
        _h.Library.Search["office"] = [new MediaItem { Id = "s1", Name = "The Office", Type = "Series" }];
        _h.Library.Seasons["s1"] = [new MediaItem { Id = "se1", Name = "Season 1" }, new MediaItem { Id = "se2", Name = "Season 2" }];
        _h.Library.Episodes["se2"] = [MediaHarness.Episode("e1", "The Office", 2, 1, "The Dundies"), new MediaItem { Id = "e2", Name = "Special", Type = "Episode", SeriesName = "The Office" }];
        var run = _h.Start("!media office");
        await _h.UntilReply("**The Office** — 2 seasons. Pick a season:\n\n  `1` Season 1\n  `2` Season 2");
        await _h.Answer("2");
        await _h.UntilReply("**The Office — Season 2**. Pick an episode:\n\n  `1` E01 — The Dundies\n  `2` Special");
        await _h.Answer("1");
        await run;
        Assert.Equal("The Office — S02E01 — The Dundies", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Single_season_skips_the_season_picker_and_labels_without_codes()
    {
        _h.Library.Search["show"] = [new MediaItem { Id = "s1", Type = "Series" }];
        _h.Library.Seasons["s1"] = [new MediaItem { Id = "se1" }];
        _h.Library.Episodes["se1"] = [new MediaItem { Id = "e1", Name = "Pilot", Type = "Episode", SeriesName = "Show" }];
        var run = _h.Start("!media show");
        await _h.UntilReply("**Unknown — Unknown**. Pick an episode:\n\n  `1` Pilot");
        await _h.Answer("1");
        await run;
        Assert.Equal("Show — Pilot", _h.Current.Request.Title);
    }

    [Theory]
    [InlineData("show s01e02", "S01E02 of `show` not found in Jellyfin")]
    [InlineData("zzz", "nothing found in Jellyfin for `zzz`")]
    public async Task Nothing_found(string query, string reply)
    {
        await _h.Send($"!media {query}");
        Assert.Equal([reply], _h.Texts);
    }

    [Fact]
    public async Task Episode_code_finds_the_episode_directly()
    {
        _h.Library.Search["the office 2x3"] = [MediaHarness.Episode("e", "The Office", 2, 3, "Office Olympics")];
        await _h.Send("!media The Office S02E03");
        Assert.Equal("The Office — S02E03 — Office Olympics", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Search_errors_distinguish_unreachable()
    {
        _h.Library.SearchError = new JellyfinException("Jellyfin request failed", new HttpRequestException("connection refused"));
        await _h.Send("!media x");
        _h.Library.SearchError = new JellyfinException("HTTP 500");
        await _h.Send("!media x");
        Assert.Equal(["could not reach Jellyfin: Jellyfin request failed", "Jellyfin search failed: HTTP 500"], _h.Texts);
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
        await _h.Send("!media bad");
        await _h.Send("!media empty");
        await _h.Send("!media eps");
        await _h.Send("!media noeps");
        Assert.Equal(
            ["failed to fetch seasons: seasons down", "no seasons found for **Empty**", "failed to fetch episodes: episodes down", "no episodes found for **S1** of **NoEps**"],
            _h.Texts);
    }

    [Fact]
    public async Task Not_in_voice()
    {
        _h.Gateway.States.Clear();
        _h.Library.Search["inception"] = [MediaHarness.Movie("m1", "Inception")];
        await _h.Send("!media inception");
        Assert.Equal(["you're not in a voice channel"], _h.Texts);
    }

    [Fact]
    public async Task Playback_failures_reply_and_release_the_transcode()
    {
        _h.Library.Search["x"] = [MediaHarness.Movie("m1", "X", null)];
        _h.Library.NoPlayback = true;
        await _h.Send("!media x");
        _h.Library.NoPlayback = false;

        _h.Library.UnknownMetadata = true;
        await _h.Send("!media x");                       // probe fails
        _h.Library.Probe = new ProbeResult("vp9", 24, "p", false, 1, 1, false, null);
        await _h.Send("!media x");                       // probed codec unsupported
        _h.Library.UnknownMetadata = false;
        _h.Library.Codec = "av1";
        await _h.Send("!media x");                       // PlaybackInfo codec unsupported

        Assert.Equal(
            [
                "checking stream…", "could not start a Jellyfin playback session for **X** — check that the server is reachable and the credentials are valid",
                "checking stream…", "could not probe the Jellyfin stream for **X** — transcoding may have failed on the server",
                "checking stream…", "unsupported video codec `vp9` from Jellyfin for **X**",
                "checking stream…", "unsupported video codec `av1` from Jellyfin for **X**",
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
        await _h.Send("!media x");
        Assert.False(_h.Current.Request.HasAudio);
    }

    [Fact]
    public async Task Go_live_failure_releases_the_transcode_and_clears_now_playing()
    {
        _h.Library.Search["x"] = [MediaHarness.Movie("m1", "X")];
        _h.Connector.Next.Enqueue(_ => Task.FromException<IGoLiveLink>(new TimeoutException("slow")));
        await _h.Send("!media x");
        Assert.Equal("go-live connection failed: slow", _h.Texts[^1]);
        Assert.Equal(["ps1"], _h.Library.Stopped);
        await _h.Send("!media playlist");
        Assert.Equal("nothing playing and the playlist is empty", _h.Texts[^1]);
    }

    [Fact]
    public async Task Replacing_an_item_releases_the_previous_transcode()
    {
        _h.Library.Search["a"] = [MediaHarness.Movie("a", "A")];
        _h.Library.Search["b"] = [MediaHarness.Movie("b", "B")];
        await _h.Send("!media a");
        await _h.Send("!media b");
        Assert.Equal(["ps1"], _h.Library.Stopped);
        Assert.Equal(1, _h.Pipelines.Created[0].Stops);
    }

    [Fact]
    public async Task Search_subcommand_and_title_fallthrough()
    {
        _h.Library.Search["queue"] = [MediaHarness.Movie("q", "Queue")];
        _h.Library.Search["stop making sense"] = [MediaHarness.Movie("s", "Stop Making Sense", 1984)];
        await _h.Send("!media search queue");
        Assert.Equal("Queue (2010)", _h.Current.Request.Title);
        await _h.Send("!media stop making sense");
        Assert.Equal("Stop Making Sense (1984)", _h.Current.Request.Title);
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
        media.Register(router);
        await router.DispatchAsync(new ChatMessage(5, 7, 20, "!media x"), TestContext.Current.CancellationToken);
        Assert.Equal("could not join the voice channel: no permission", h.Texts[^1]);
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

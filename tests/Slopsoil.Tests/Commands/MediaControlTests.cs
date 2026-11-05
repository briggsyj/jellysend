using Slopsoil.Commands.Media;
using Slopsoil.Streaming.Engine;

namespace Slopsoil.Tests.Commands;

public sealed class MediaControlTests
{
    private readonly MediaHarness _h = new();

    public MediaControlTests()
    {
        _h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000, 2L * 3600 * 10_000_000)];
        _h.Library.Search["other"] = [MediaHarness.Movie("o", "Other", 2001)];
    }

    private async Task PlayFilm()
    {
        await _h.Send("!play film");
        _h.Sender.Sent.Clear();
    }

    [Fact]
    public async Task Pause_and_play()
    {
        await _h.Send("!media pause");
        await _h.Send("!media play");
        await PlayFilm();
        _h.Current.Fake.Position = 65;
        await _h.Send("!media pause");
        await _h.Send("!media pause");
        await _h.Send("!media play");
        await _h.Send("!media play");
        Assert.Equal(["⏸ Paused at 0:01:05 — we'll be right back. `!play` to carry on.", "⏸ Already paused — `!play` to carry on.", "▶️ Back on air at 0:01:05", "▶️ Already on air."], _h.Texts);
        Assert.Equal(["pause", "resume"], _h.Current.Fake.Calls);
    }

    [Fact]
    public async Task Idle_pause_and_play_replies()
    {
        await _h.Send("!media pause");
        await _h.Send("!media play");
        Assert.Equal(["🙅 Nothing's on air right now.", "🙅 Nothing's paused — `!play <title>` to put something on."], _h.Texts);
    }

    [Fact]
    public async Task Resume_seeks_in_place_and_validates()
    {
        await _h.Send("!time 1:00:00");
        await PlayFilm();
        await _h.Send("!media pause");
        await _h.Send("!time 1:00:00");
        await _h.Send("!time 2:00:00");
        await _h.Send("!time 1:75");
        Assert.Equal(
            ["⏸ Paused at 0:00:00 — we'll be right back. `!play` to carry on.", "⏩ Jumping to 1:00:00", "🙅 2:00:00 is past the end (2:00:00).",
             "🙅 `1:75` isn't a time — try `1:23:45`, `1.23.45`, `1:23` (h:mm), `45m` or `90s`."],
            _h.Texts);
        Assert.Equal(["pause", "seek 3600", "resume"], _h.Current.Fake.Calls);
    }

    [Fact]
    public async Task Time_shows_the_position_and_old_resume_points_to_play_and_time()
    {
        await _h.Send("!time");
        Assert.Equal(["🙅 Nothing's on air right now."], _h.Texts);
        await PlayFilm();
        _h.Current.Fake.Position = 65;
        await _h.Send("!time");
        Assert.Equal("⏱ 0:01:05 / 2:00:00", _h.Texts[^1]);
        _h.Current.Fake.IsPaused = true;
        await _h.Send("!media time");
        Assert.Equal("⏱ 0:01:05 / 2:00:00 (paused)", _h.Texts[^1]);
        await _h.Send("!media resume");
        Assert.StartsWith("-# `!media resume` is gone: `!play` resumes, `!time <time>` jumps\n\n### 📺 Slopsoil — now broadcasting", _h.Texts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Time_shows_the_runtime_when_known()
    {
        _h.Library.Search["long"] = [MediaHarness.Movie("l", "Long", 2000) with { RunTimeTicks = 5400L * 10_000_000 }];
        await _h.Send("!play long");
        _h.Current.Fake.Position = 61;
        await _h.Send("!time");
        Assert.Equal("⏱ 0:01:01 / 1:30:00", _h.Texts[^1]);
    }

    [Fact]
    public async Task Play_with_only_a_time_jumps_while_playing_and_searches_otherwise()
    {
        _h.Library.Search["3:33"] = [MediaHarness.Movie("t", "3:33", 2017)];
        await _h.Send("!play 3:33");
        Assert.Equal("3:33 (2017)", _h.Current.Request.Title); // nothing playing: a title
        _h.Sender.Sent.Clear();
        await _h.Send("!play 0:10");
        Assert.Equal(["⏩ Jumping to 0:10:00"], _h.Texts);
        Assert.Equal(600, _h.Current.Fake.Position);
    }

    [Fact]
    public async Task Media_leave_stops_tears_down_and_leaves()
    {
        await _h.Send("!media leave");
        Assert.Equal(["🙅 I'm not in a voice channel."], _h.Texts);
        await PlayFilm();
        await _h.Send("!media leave");
        Assert.Equal(["📴 Signing off — left the voice channel."], _h.Texts);
        Assert.False(_h.Media.IsStreaming(5));
        Assert.False(_h.Gateway.Links[0].IsConnected);
        Assert.Equal(["ps1"], _h.Library.Stopped);
    }

    [Fact]
    public async Task Voice_stop_keeps_voice_and_playlist_leave_drops_both()
    {
        await PlayFilm();
        await _h.Send("!media playlist add other");
        await _h.Send("!stop");
        Assert.True(_h.Gateway.Links[0].IsConnected);
        Assert.Equal("📜 **Coming up** (1/10):\n  `1` Other (2001)", await _h.ShowPlaylist("!media playlist"));
        await _h.Send("!leave");
        Assert.Equal("📭 Nothing on air and nothing on the playlist.", await _h.ShowPlaylist("!media playlist"));
    }

    [Fact]
    public async Task Aspect_picker_marks_current_applies_live_and_is_remembered()
    {
        await PlayFilm();
        var run = _h.Start("!media aspect-ratio");
        await _h.UntilReply(
            "📐 **Aspect ratio** — reply with a number or name:\n`1` Original (default) — keep the media's own shape  ← current\n`2` 16:9 — widescreen\n"
            + "`3` 4:3 — classic TV\n`4` 2.39:1 — cinema scope (21:9)\n`5` 1.85:1 — theatrical widescreen");
        await _h.Answer("3");
        await run;
        Assert.Equal("📐 Aspect ratio set to **4:3** — adjusting the picture…", _h.Texts[^1]);
        Assert.Equal(4.0 / 3.0, _h.Current.Fake.Aspect);

        await _h.Send("!media ar 3");                 // same ratio: nothing to restart
        Assert.Equal("📐 Aspect ratio set to **4:3**.", _h.Texts[^1]);
        await _h.Send("!leave");
        await _h.Send("!play film");
        Assert.Equal(4.0 / 3.0, _h.Current.Request.Aspect); // remembered across leaving voice
        await _h.Send("!media aspect 1");
        Assert.Null(_h.Current.Fake.Aspect);
    }

    [Fact]
    public async Task Aspect_takes_names_numbers_and_shows_the_picker_for_unknown_words()
    {
        _h.Library.Search["ar sample"] = [MediaHarness.Movie("r", "AR Sample Live", 2015)];
        await _h.Send("!media ar 9");
        await _h.Send("!ar widescreen");
        Assert.Equal(["🙅 Pick a number between 1 and 5 — see `!ar`.", "📐 Aspect ratio set to **16:9**."], _h.Texts);
        var run = _h.Start("!ar sample");
        await _h.UntilReply("🙅 `sample` isn't a picture shape. 📐 **Aspect ratio** — reply with a number or name:\n`1` Original (default) — keep the media's own shape\n`2` 16:9 — widescreen  ← current\n"
            + "`3` 4:3 — classic TV\n`4` 2.39:1 — cinema scope (21:9)\n`5` 1.85:1 — theatrical widescreen");
        await _h.Answer("scope");
        await run;
        Assert.Equal("📐 Aspect ratio set to **2.39:1**.", _h.Texts[^1]);
        await _h.Send("!media aspect 2", guild: 9, author: 20);
        Assert.Equal("📐 Aspect ratio set to **16:9**.", _h.Texts[^1]);
    }

    [Fact]
    public async Task Aspect_with_nobody_in_voice_anywhere()
    {
        _h.Gateway.States.Clear();
        await _h.Send("!media ar 2", guild: null);
        Assert.Equal(["🙅 Join a voice channel first, then try again."], _h.Texts);
    }

    [Fact]
    public async Task Debug_toggles_the_overlay_and_reports()
    {
        await _h.Send("!media debug");
        await _h.Send("!media debug on");
        Assert.Equal(["🐞 Stats overlay is **off** — nothing's on air.", "🐞 Stats overlay **on** — from the next programme."], _h.Texts);
        await PlayFilm();
        Assert.True(_h.Current.Request.DebugOverlay);
        await _h.Send("!media debug");
        await _h.Send("!media debug OFF");
        await _h.Send("!media debug off");
        await _h.Send("!media debug on");
        Assert.Equal(
            ["🐞 Stats overlay is **on**\n```\ndiag\n```", "🐞 Stats overlay **off**.", "🐞 Stats overlay **off**.", "🐞 Stats overlay **on**."],
            _h.Texts);
        await _h.Send("!leave");
        await _h.Send("!play film");
        Assert.False(_h.Current.Request.DebugOverlay); // cleared when leaving voice
    }

    [Fact]
    public async Task Debug_with_an_unknown_word_shows_its_help()
    {
        await PlayFilm();
        await _h.Send("!media debug mode");
        Assert.Equal(CommandCatalog.Find("debug")!.HelpText(), _h.Texts[^1]);
        _h.Gateway.States.Clear();
        await _h.Send("!media debug on", guild: null);
        Assert.Equal("🐞 Stats overlay **on**.", _h.Texts[^1]); // DM: applies where the bot is in voice
    }

    [Fact]
    public async Task Play_takes_titles_that_start_with_command_words()
    {
        _h.Library.Search["pause the sample"] = [MediaHarness.Movie("p", "Pause The Sample", 2003)];
        _h.Library.Search["play sample"] = [MediaHarness.Movie("t", "Play Sample", 1967)];
        await _h.Send("!play pause the sample");
        await _h.Send("!media play play sample");
        Assert.Equal("Play Sample (1967)", _h.Current.Request.Title);
        Assert.Equal(2, _h.Pipelines.Created.Count);
    }

    [Fact]
    public async Task No_argument_commands_ignore_extra_words()
    {
        await PlayFilm();
        await _h.Send("!pause the sample");
        Assert.Equal("⏸ Paused at 0:00:00 — we'll be right back. `!play` to carry on.", _h.Texts[^1]);
        Assert.Single(_h.Pipelines.Created);
    }

    [Fact]
    public async Task Leave_during_a_start_abandons_it_without_rejoining()
    {
        await PlayFilm();
        _h.Library.PlaybackGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Library.PlaybackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = _h.Start("!play other");
        await _h.Library.PlaybackEntered.Task;
        await _h.Send("!leave");
        _h.Library.PlaybackGate.SetResult();
        await start;

        Assert.Single(_h.Pipelines.Created);        // the new item never streamed
        Assert.Single(_h.Gateway.Links);            // and the bot did not rejoin
        Assert.False(_h.Media.IsStreaming(5));
        Assert.Contains("ps2", _h.Library.Stopped); // its transcode was released
    }

    [Fact]
    public async Task A_failed_stream_releases_its_transcode_and_advances()
    {
        await PlayFilm();
        await _h.Send("!media playlist add other");
        _h.Current.End(PipelineEndReason.Failed, "go-live error: encoder failed");
        await _h.Until(() => _h.Pipelines.Created.Count == 2 && _h.Current.Started);
        Assert.Equal("Other (2001)", _h.Current.Request.Title);
        Assert.Contains("ps1", _h.Library.Stopped);
        Assert.Contains("go-live error: encoder failed", _h.Texts);
    }

    [Fact]
    public async Task Kicked_bot_can_still_be_stopped_and_media_stop_accepts_a_stale_link()
    {
        await PlayFilm();
        _h.Gateway.Links[0].IsConnected = false;   // kicked
        await _h.Send("!media leave");
        Assert.Equal(["📴 Signing off — left the voice channel."], _h.Texts);
        Assert.False(_h.Media.IsStreaming(5));
    }

    [Fact]
    public async Task Dropped_screenshare_resumes_the_same_item_at_its_position_up_to_three_times()
    {
        await PlayFilm();
        await _h.Send("!media playlist add other");
        for (var attempt = 1; attempt <= MediaModule.MaxReconnects; attempt++)
        {
            var count = _h.Pipelines.Created.Count;
            _h.Current.Fake.Position = 100 * attempt;
            _h.Connector.Links[^1].Close();
            await _h.Until(() => _h.Pipelines.Created.Count == count + 1 && _h.Current.Started);
            Assert.Equal(("Film (2000)", 100.0 * attempt), (_h.Current.Request.Title, _h.Current.Request.StartOffset));
        }

        // Budget spent: the next drop ends the item and the playlist advances.
        _h.Connector.Links[^1].Close();
        await _h.Until(() => _h.Current.Request.Title == "Other (2001)" && _h.Current.Started);
        Assert.Contains(Slopsoil.Streaming.Engine.StreamEngine.ConnectionLostMessage, _h.Texts);
    }

    [Fact]
    public async Task Stop_during_auto_advance_keeps_the_playlist()
    {
        await PlayFilm();
        await _h.Send("!media playlist add other");
        _h.Library.Search["third"] = [MediaHarness.Movie("t", "Third", 2002)];
        await _h.Send("!media playlist add third");
        _h.Library.PlaybackGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Library.PlaybackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Current.End(PipelineEndReason.Natural);       // auto-advance starts "Other"…
        await _h.Library.PlaybackEntered.Task;
        await _h.Send("!stop");                          // …and the user stops meanwhile
        _h.Library.PlaybackGate.SetResult();
        await _h.Until(() => !_h.Media.IsStarting(5));
        Assert.Single(_h.Pipelines.Created);             // neither Other nor Third started
        Assert.Equal("📜 **Coming up** (2/10):\n  `1` Other (2001)\n  `2` Third (2002)", await _h.ShowPlaylist("!media playlist"));
    }

    [Fact]
    public async Task No_reconnect_after_a_kick_and_no_false_reconnect_message()
    {
        await PlayFilm();
        _h.Gateway.States.Remove((5, 1));                // kicked: the bot is no longer in the channel
        _h.Connector.Links[^1].Close();
        await _h.Until(() => !_h.Media.IsStreaming(5) && !_h.Media.IsStarting(5));
        Assert.Single(_h.Pipelines.Created);
        Assert.Contains(Slopsoil.Streaming.Engine.StreamEngine.ConnectionLostMessage, _h.Texts);
        Assert.DoesNotContain(_h.Texts, t => t.Contains("reconnecting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Media_stop_stops_but_stays_in_voice_and_keeps_the_playlist()
    {
        await PlayFilm();
        await _h.Send("!media playlist add other");
        await _h.Send("!media stop");
        Assert.Equal(Slopsoil.Commands.Voice.VoiceModule.StoppedReply, _h.Texts[^1]);
        Assert.False(_h.Media.IsStreaming(5));
        Assert.True(_h.Gateway.Links[0].IsConnected);
        Assert.Equal("📜 **Coming up** (1/10):\n  `1` Other (2001)", await _h.ShowPlaylist("!media playlist"));
    }

    [Theory]
    [InlineData("!join", "!media join")]
    [InlineData("!stop", "!media stop")]
    [InlineData("!leave", "!media leave")]
    [InlineData("!help", "!media help")]
    [InlineData("!ping", "!media ping")]
    public async Task Shortcuts_reply_exactly_like_their_media_command(string shortcut, string full)
    {
        await PlayFilm();
        await _h.Send(full);
        var viaFull = _h.Texts.ToList();
        _h.Sender.Sent.Clear();
        await PlayFilm();
        await _h.Send(shortcut);
        Assert.Equal(viaFull, _h.Texts);
    }

    [Fact]
    public async Task Voice_commands_ignore_extra_words()
    {
        await PlayFilm();
        await _h.Send("!media leave the sample");
        Assert.Equal(Slopsoil.Commands.Voice.VoiceModule.LeftReply, _h.Texts[^1]);
        Assert.False(_h.Media.IsStreaming(5));
    }

    [Fact]
    public async Task Help_and_ping_are_open_to_viewers_but_playback_is_not()
    {
        await _h.Send("!media help", author: 30);
        await _h.Send("!ping", author: 30);
        await _h.Send("!help sample film", author: 30);      // not a command: never a search
        await _h.Send("!play film", author: 30);           // viewers can't play
        await _h.Send("!join", author: 30);
        Assert.Equal([MediaText.HelpText, "🏓 Pong! Still broadcasting.", "🙅 No command called `sample film` — `!help` lists them all.", "_quack_ no.", "_quack_ no."], _h.Texts);
        Assert.Empty(_h.Pipelines.Created);
    }

    [Theory]
    [InlineData("!channels")]
    [InlineData("!search news")]
    [InlineData("!add-source a b")]
    [InlineData("!sources")]
    [InlineData("!delete-source")]
    [InlineData("!hello")]
    public async Task Removed_commands_are_silent(string content)
    {
        await _h.Send(content, author: 10);
        Assert.Empty(_h.Texts);
    }

    [Fact]
    public async Task Pipeline_failure_message_reaches_the_channel()
    {
        await PlayFilm();
        _h.Current.End(PipelineEndReason.Failed, FfmpegPipeline.FailedToStart);
        await _h.UntilReply(FfmpegPipeline.FailedToStart);
    }
}

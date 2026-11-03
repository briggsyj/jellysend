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
        await _h.Send("!media film");
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
        Assert.Equal(["⏸ paused at 0:01:05", "already paused — `!media play` to carry on", "▶ resumed at 0:01:05", "already playing"], _h.Texts);
        Assert.Equal(["pause", "resume"], _h.Current.Fake.Calls);
    }

    [Fact]
    public async Task Idle_pause_and_play_replies()
    {
        await _h.Send("!media pause");
        await _h.Send("!media play");
        Assert.Equal(["nothing is playing", "nothing is paused — use `!media <title>` to play something"], _h.Texts);
    }

    [Fact]
    public async Task Resume_seeks_in_place_and_validates()
    {
        await _h.Send("!media resume 1:00:00");
        await PlayFilm();
        await _h.Send("!media pause");
        await _h.Send("!media resume 1:00:00");
        await _h.Send("!media resume 2:00:00");
        await _h.Send("!media resume 1:75");
        Assert.Equal(
            ["⏸ paused at 0:00:00", "⏩ jumping to 1:00:00", "2:00:00 is past the end (2:00:00)",
             "`1:75` isn't a time — try `1:23:45`, `1.23.45`, `1:23` (h:mm), `45m` or `90s`"],
            _h.Texts);
        Assert.Equal(["pause", "seek 3600", "resume"], _h.Current.Fake.Calls);
    }

    [Fact]
    public async Task Resume_with_words_is_a_title_and_bare_resume_is_play()
    {
        _h.Library.Search["resume the film"] = [MediaHarness.Movie("r", "Resume The Film", 1999)];
        await _h.Send("!media resume the film");
        Assert.Equal("Resume The Film (1999)", _h.Current.Request.Title);
        _h.Sender.Sent.Clear();
        await _h.Send("!media resume");
        Assert.Equal(["already playing"], _h.Texts);
    }

    [Fact]
    public async Task Media_stop_stops_tears_down_and_leaves()
    {
        await _h.Send("!media stop");
        Assert.Equal(["not in a voice channel"], _h.Texts);
        await PlayFilm();
        await _h.Send("!media stop");
        Assert.Equal(["⏹ stopped and left the voice channel"], _h.Texts);
        Assert.False(_h.Media.IsStreaming(5));
        Assert.False(_h.Gateway.Links[0].IsConnected);
        Assert.Equal(["ps1"], _h.Library.Stopped);
    }

    [Fact]
    public async Task Voice_stop_keeps_voice_and_playlist_leave_drops_both()
    {
        await PlayFilm();
        await _h.Send("!media queue other");
        await _h.Send("!stop");
        Assert.True(_h.Gateway.Links[0].IsConnected);
        await _h.Send("!media playlist");
        Assert.Equal("**Up next** (1/10):\n  `1` Other (2001)", _h.Texts[^1]);
        await _h.Send("!leave");
        await _h.Send("!media playlist");
        Assert.Equal("nothing playing and the playlist is empty", _h.Texts[^1]);
    }

    [Fact]
    public async Task Aspect_picker_marks_current_applies_live_and_is_remembered()
    {
        await PlayFilm();
        var run = _h.Start("!media aspect-ratio");
        await _h.UntilReply(
            "**Aspect ratio** — reply with a number:\n`1` Original (default) — keep the media's own shape  ← current\n`2` 16:9 — widescreen\n"
            + "`3` 4:3 — classic TV\n`4` 2.39:1 — cinema scope (21:9)\n`5` 1.85:1 — theatrical widescreen");
        await _h.Answer("3");
        await run;
        Assert.Equal("📐 aspect ratio set to **4:3** — applying now", _h.Texts[^1]);
        Assert.Equal(4.0 / 3.0, _h.Current.Fake.Aspect);

        await _h.Send("!media ar 3");                 // same ratio: nothing to restart
        Assert.Equal("📐 aspect ratio set to **4:3**", _h.Texts[^1]);
        await _h.Send("!leave");
        await _h.Send("!media film");
        Assert.Equal(4.0 / 3.0, _h.Current.Request.Aspect); // remembered across leaving voice
        await _h.Send("!media aspect 1");
        Assert.Null(_h.Current.Fake.Aspect);
    }

    [Fact]
    public async Task Aspect_validation_and_title_fallthrough()
    {
        _h.Library.Search["ar rahman"] = [MediaHarness.Movie("r", "AR Rahman Live", 2015)];
        await _h.Send("!media ar 9");
        await _h.Send("!media ar 2x");
        Assert.Equal(["pick a number between 1 and 5 — see `!media aspect-ratio`", "pick a number between 1 and 5 — see `!media aspect-ratio`"], _h.Texts);
        await _h.Send("!media ar rahman");
        Assert.Equal("AR Rahman Live (2015)", _h.Current.Request.Title);
        await _h.Send("!media aspect 2", guild: 9, author: 20);
        Assert.Equal("📐 aspect ratio set to **16:9**", _h.Texts[^1]);
    }

    [Fact]
    public async Task Aspect_with_nobody_in_voice_anywhere()
    {
        _h.Gateway.States.Clear();
        await _h.Send("!media ar 2", guild: null);
        Assert.Equal(["you're not in a voice channel"], _h.Texts);
    }

    [Fact]
    public async Task Debug_toggles_the_overlay_and_reports()
    {
        await _h.Send("!media debug");
        await _h.Send("!media debug on");
        Assert.Equal(["🐞 debug overlay is **off** — nothing is playing", "🐞 debug overlay **on** — applies to the next item"], _h.Texts);
        await PlayFilm();
        Assert.True(_h.Current.Request.DebugOverlay);
        await _h.Send("!media debug");
        await _h.Send("!media debug OFF");
        await _h.Send("!media debug off");
        await _h.Send("!media debug on");
        Assert.Equal(
            ["🐞 debug overlay is **on**\n```\ndiag\n```", "🐞 debug overlay **off** — applying now", "🐞 debug overlay **off**", "🐞 debug overlay **on** — applying now"],
            _h.Texts);
        await _h.Send("!leave");
        await _h.Send("!media film");
        Assert.False(_h.Current.Request.DebugOverlay); // cleared when leaving voice
    }

    [Fact]
    public async Task Debug_words_are_a_title()
    {
        _h.Library.Search["debug mode"] = [MediaHarness.Movie("d", "Debug Mode", 2020)];
        await _h.Send("!media debug mode");
        Assert.Equal("Debug Mode (2020)", _h.Current.Request.Title);
        _h.Gateway.States.Clear();
        await _h.Send("!media debug on", guild: null);
        Assert.Equal("🐞 debug overlay **on** — applying now", _h.Texts[^1]); // DM: applies where the bot is in voice
    }

    [Fact]
    public async Task Pause_and_play_fall_through_to_titles()
    {
        _h.Library.Search["pause the world"] = [MediaHarness.Movie("p", "Pause The World", 2003)];
        _h.Library.Search["play time"] = [MediaHarness.Movie("t", "Play Time", 1967)];
        await _h.Send("!media pause the world");
        await _h.Send("!media play time");
        Assert.Equal("Play Time (1967)", _h.Current.Request.Title);
        Assert.Equal(2, _h.Pipelines.Created.Count);
    }

    [Fact]
    public async Task Leave_during_a_start_abandons_it_without_rejoining()
    {
        await PlayFilm();
        _h.Library.PlaybackGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Library.PlaybackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = _h.Start("!media other");
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
        await _h.Send("!media queue other");
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
        await _h.Send("!media stop");
        Assert.Equal(["⏹ stopped and left the voice channel"], _h.Texts);
        Assert.False(_h.Media.IsStreaming(5));
    }

    [Fact]
    public async Task Dropped_screenshare_resumes_the_same_item_at_its_position_up_to_three_times()
    {
        await PlayFilm();
        await _h.Send("!media queue other");
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
        await _h.Send("!media queue other");
        _h.Library.Search["third"] = [MediaHarness.Movie("t", "Third", 2002)];
        await _h.Send("!media queue third");
        _h.Library.PlaybackGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Library.PlaybackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Current.End(PipelineEndReason.Natural);       // auto-advance starts "Other"…
        await _h.Library.PlaybackEntered.Task;
        await _h.Send("!stop");                          // …and the user stops meanwhile
        _h.Library.PlaybackGate.SetResult();
        await _h.Until(() => !_h.Media.IsStarting(5));
        Assert.Single(_h.Pipelines.Created);             // neither Other nor Third started
        await _h.Send("!media playlist");
        Assert.Equal("**Up next** (2/10):\n  `1` Other (2001)\n  `2` Third (2002)", _h.Texts[^1]);
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
    public async Task Pipeline_failure_message_reaches_the_channel()
    {
        await PlayFilm();
        _h.Current.End(PipelineEndReason.Failed, "go-live stream failed to start");
        await _h.UntilReply("go-live stream failed to start");
    }
}

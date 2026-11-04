using Slopsoil.Commands.Media;

namespace Slopsoil.Tests.Commands;

public sealed class MediaSyncPlayTests
{
    private readonly MediaHarness _h = new();

    public MediaSyncPlayTests()
    {
        _h.Library.HasUserCredentials = true;
        _h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000)];
        _h.Library.Items["o"] = MediaHarness.Movie("o", "Other", 2001);
        _h.Library.Items["f"] = MediaHarness.Movie("f", "Film", 2000);
    }

    private async Task<FakeSyncPlay> PlayAndSync(double shownAt = 12)
    {
        await _h.Send("!play film");
        _h.Current.Fake.Position = shownAt;
        _h.Current.Fake.FirstFrame.SetResult();
        await _h.Drain();
        return Assert.Single(_h.Library.Groups);
    }

    [Fact]
    public async Task Group_is_created_and_queued_from_the_first_shown_frame()
    {
        var g = await PlayAndSync();
        Assert.Equal(["open slopsoil · Movie Night", "queue f 12"], g.Calls);
        Assert.Contains("👥 Jellyfin SyncPlay group **slopsoil · Movie Night** is open — join it from a Jellyfin app to control playback together", _h.Texts);
    }

    [Fact]
    public async Task Local_controls_are_forwarded()
    {
        var g = await PlayAndSync();
        await _h.Send("!media pause");
        await _h.Send("!time 0:10");
        await _h.Send("!media pause");
        await _h.Send("!media play");
        Assert.Equal(["open slopsoil · Movie Night", "queue f 12", "pause", "seek 600", "unpause", "pause", "unpause"], g.Calls);
    }

    [Fact]
    public async Task Remote_commands_drive_the_player_with_drift_tolerance()
    {
        var g = await PlayAndSync(100);
        _h.Time.Advance(TimeSpan.FromSeconds(6)); // outside the own-command window
        await g.Command("Pause", 100);
        await g.Command("Pause", 100);            // already paused: no reply
        await g.Command("Unpause", 101);          // within 3 s: no seek
        await g.Command("Seek", 102);             // within tolerance: ignored
        await g.Command("Seek", 500);
        await g.Command("Unpause", 900);          // not paused but far: seek, no reply
        Assert.Equal(["pause", "resume", "seek 500", "seek 900"], _h.Current.Fake.Calls);
        Assert.Equal(["⏸ paused from Jellyfin at 0:01:40", "▶ resumed from Jellyfin", "⏩ Jellyfin jumped to 0:08:20"], _h.Texts.Skip(_h.Texts.Count - 3));
    }

    [Fact]
    public async Task Echo_of_our_own_seek_does_not_seek_again()
    {
        var g = await PlayAndSync(100);
        await _h.Send("!media pause");
        await g.Command("Seek", 700);             // within 5 s of our own command → drift ignored
        Assert.Equal(["pause"], _h.Current.Fake.Calls);
    }

    [Fact]
    public async Task Remote_stop_stops_the_stream()
    {
        var g = await PlayAndSync();
        await g.Command("Stop", null);
        Assert.Equal("⏹ stopped from Jellyfin", _h.Texts[^1]);
        Assert.False(_h.Media.IsStreaming(5));
        await g.Command("Pause", 1);              // nothing playing: ignored
    }

    [Fact]
    public async Task Remote_queue_switch_plays_the_new_item_without_echoing()
    {
        var g = await PlayAndSync();
        await g.Queue("f", 0);                    // same item: ignored
        await g.Queue(null, 0);
        await g.Queue("missing", 0);
        await g.Queue("o", 30);
        Assert.Equal("🔀 Jellyfin group switched to **Other (2001)**", _h.Texts.First(t => t.StartsWith("🔀", StringComparison.Ordinal)));
        Assert.Equal(("Other (2001)", 30.0), (_h.Current.Request.Title, _h.Current.Request.StartOffset));
        await _h.Drain();
        Assert.Equal(2, g.Calls.Count);           // sync: false — not re-queued to the group
    }

    [Fact]
    public async Task Our_own_queue_echo_is_ignored()
    {
        var g = await PlayAndSync();
        await _h.Send("!stop");
        await g.Queue("f", 0);                    // we just queued f: echo within 10 s
        Assert.Single(_h.Pipelines.Created);
        _h.Time.Advance(TimeSpan.FromSeconds(11));
        await g.Queue("f", 0);
        Assert.Equal(2, _h.Pipelines.Created.Count);
    }

    [Fact]
    public async Task Queue_switch_needs_voice()
    {
        var g = await PlayAndSync();
        _h.Gateway.Links[0].IsConnected = false;
        await g.Queue("o", 0);
        Assert.Single(_h.Pipelines.Created);
    }

    [Fact]
    public async Task Group_reused_until_closed_and_closed_on_leave()
    {
        var g = await PlayAndSync();
        _h.Library.Search["film2"] = [MediaHarness.Movie("f2", "Film2", 2002)];
        await _h.Send("!play film2");
        _h.Current.Fake.FirstFrame.SetResult();
        await _h.Drain();
        Assert.Single(_h.Library.Groups);
        Assert.Equal("queue f2 0", g.Calls[^1]);
        await _h.Send("!leave");
        Assert.Equal("close", g.Calls[^1]);
    }

    [Fact]
    public async Task Open_failure_means_no_group()
    {
        _h.Library.FailSyncOpen = true;
        await _h.Send("!play film");
        _h.Current.Fake.FirstFrame.SetResult();
        await _h.Drain();
        Assert.Equal(["open slopsoil · Movie Night", "close"], _h.Library.Groups[0].Calls);
        await _h.Send("!media pause");             // no group: nothing forwarded
        Assert.Equal(2, _h.Library.Groups[0].Calls.Count);
    }

    [Fact]
    public async Task Replaced_before_the_first_frame_is_not_queued()
    {
        await _h.Send("!play film");
        var first = _h.Current;
        await _h.Send("!play film");
        first.Fake.FirstFrame.SetResult();
        _h.Current.Fake.FirstFrame.SetResult();
        await _h.Drain();
        Assert.Equal(["open slopsoil · Movie Night", "queue f 0"], _h.Library.Groups[0].Calls);
    }

    [Fact]
    public async Task Without_a_login_nothing_is_synced()
    {
        _h.Library.HasUserCredentials = false;
        await _h.Send("!play film");
        await _h.Drain();
        Assert.Empty(_h.Library.Groups);
    }
}

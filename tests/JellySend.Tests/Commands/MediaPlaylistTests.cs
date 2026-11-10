using JellySend.Commands.Media;
using JellySend.Streaming.Engine;

namespace JellySend.Tests.Commands;

public sealed class MediaPlaylistTests
{
    private readonly MediaHarness _h = new();

    public MediaPlaylistTests()
    {
        foreach (var n in new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l" })
        {
            _h.Library.Search[n] = [MediaHarness.Movie(n, n.ToUpperInvariant(), null)];
        }
    }

    [Fact]
    public async Task Queue_while_idle_plays_and_while_playing_appends()
    {
        await _h.Send("!media playlist add a");
        Assert.Equal("A", _h.Current.Request.Title);
        await _h.Send("!media playlist add b");
        await _h.Send("!media playlist add c");
        Assert.Equal(["📡 Tuning in to **A**…", "📺 Now showing: **A**", "➕ Added to the playlist: **B** (#1)", "➕ Added to the playlist: **C** (#2)"], _h.Texts);
    }

    [Fact]
    public async Task Playlist_shows_now_playing_and_up_next()
    {
        _h.Library.Search["long"] = [MediaHarness.Movie("long", "Long", 2000, 90L * 60 * 10_000_000)];
        await _h.Send("!play long");
        _h.Current.Fake.Position = 61;
        Assert.Equal("📺 **Now showing:** Long (2000) — 0:01:01 / 1:30:00\n_Nothing else on the playlist._", await _h.ShowPlaylist("!media playlist"));
        await _h.Send("!media playlist add b");
        await _h.Send("!media pause");
        Assert.Equal("📺 **Now showing:** Long (2000) — 0:01:01 / 1:30:00 (paused)\n📜 **Coming up** (1/10):\n  `1` B", await _h.ShowPlaylist("!media playlist"));
        await _h.Send("!playlist please");         // an unknown action
        Assert.Equal("🙅 `!playlist` can `add`, `remove` or `order` — e.g. `!playlist add big buck bunny`. More: `!help playlist`.", _h.Texts[^1]);
    }

    [Fact]
    public async Task Playlist_caps_at_ten()
    {
        await _h.Send("!play a");
        foreach (var n in "bcdefghijk")
        {
            await _h.Send($"!media playlist add {n}");
        }

        await _h.Send("!media playlist add l");
        Assert.Equal("🙅 The playlist is full (10 items).", _h.Texts[^1]);
    }

    [Fact]
    public async Task Order_and_remove()
    {
        await _h.Send("!media playlist order 1 1");
        await _h.Send("!play a");
        await _h.Send("!media playlist add b");
        await _h.Send("!media playlist add c");
        await _h.Send("!media playlist order 2 1");
        await _h.Send("!media playlist remove B");
        Assert.Equal("📭 The playlist is empty.", _h.Texts[0]);
        Assert.Equal(["↕️ Moved **C** to #1.", "➖ Took **B** off the playlist."], _h.Texts.Skip(_h.Texts.Count - 2));
        Assert.EndsWith("📜 **Coming up** (1/10):\n  `1` C", await _h.ShowPlaylist("!media playlist"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Natural_end_auto_advances_in_order()
    {
        await _h.Send("!play a");
        await _h.Send("!media playlist add b");
        await _h.Send("!media playlist add c");
        _h.Current.End(PipelineEndReason.Natural);
        await _h.Until(() => _h.Pipelines.Created.Count == 2 && _h.Current.Started);
        Assert.Equal("B", _h.Current.Request.Title);
        Assert.Contains("⏭ Coming up next: **B**", _h.Texts);
        Assert.Contains("ps1", _h.Library.Stopped);
        _h.Current.End(PipelineEndReason.Natural);
        await _h.Until(() => _h.Pipelines.Created.Count == 3 && _h.Current.Started);
        Assert.Equal("C", _h.Current.Request.Title);
        _h.Current.End(PipelineEndReason.Natural);
        await _h.Until(() => !_h.Media.IsStreaming(5));
        Assert.Equal(3, _h.Pipelines.Created.Count);
    }

    [Fact]
    public async Task Skip()
    {
        await _h.Send("!media skip");
        await _h.Send("!play a");
        await _h.Send("!media playlist add b");
        await _h.Send("!media skip");
        Assert.Equal("B", _h.Current.Request.Title);
        await _h.Send("!media skip");
        Assert.Equal(["🙅 Nothing to skip."], _h.Texts.Take(1));
        Assert.Contains("⏭ Coming up next: **B**", _h.Texts);
        Assert.Equal("⏹ That's the end of the playlist — off air.", _h.Texts[^1]);
        Assert.False(_h.Media.IsStreaming(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, _h.Pipelines.Created.Count); // the stopped stream did not auto-advance
    }

    [Fact]
    public async Task Play_starts_a_waiting_playlist_and_skip_rejoins_voice()
    {
        await _h.Send("!play a");
        await _h.Send("!media playlist add b");
        await _h.Send("!media playlist add c");
        await _h.Send("!stop");                 // stream stops, playlist stays
        await _h.Send("!media play");
        Assert.Equal("B", _h.Current.Request.Title);
        _h.Gateway.Links[0].IsConnected = false; // kicked from voice
        await _h.Send("!media skip");
        Assert.Equal("C", _h.Current.Request.Title);
        Assert.Equal(2, _h.Gateway.Links.Count);
    }

    [Fact]
    public async Task Queue_while_idle_with_a_waiting_playlist_starts_from_its_head()
    {
        await _h.Send("!play a");
        await _h.Send("!media playlist add b");
        await _h.Send("!stop");
        _h.Sender.Sent.Clear();
        await _h.Send("!media playlist add c");
        Assert.Equal("B", _h.Current.Request.Title);
        Assert.Equal("➕ Added to the playlist: **C** (#1)", _h.Texts[0]);
    }

    [Fact]
    public async Task Failed_start_puts_the_item_back()
    {
        await _h.Send("!play a");
        await _h.Send("!media playlist add b");
        await _h.Send("!stop");
        _h.Library.NoPlayback = true;
        await _h.Send("!media play");
        Assert.Equal("📜 **Coming up** (1/10):\n  `1` B", await _h.ShowPlaylist("!media playlist"));
    }

    [Fact]
    public async Task Auto_advance_skips_items_that_fail_to_start()
    {
        await _h.Send("!play a");
        await _h.Send("!media playlist add b");
        await _h.Send("!media playlist add c");
        _h.Library.Search["b"] = [MediaHarness.Movie("b", "B", null)];
        _h.Library.Codec = "av1";
        var created = _h.Pipelines.Created.Count;
        _h.Current.End(PipelineEndReason.Natural);
        await _h.Until(() => _h.Texts.Count(t => t.Contains("uses a video format", StringComparison.Ordinal)) == 2);
        Assert.Equal(created, _h.Pipelines.Created.Count);
        await _h.Until(() => !_h.Media.IsStreaming(5));
    }
}

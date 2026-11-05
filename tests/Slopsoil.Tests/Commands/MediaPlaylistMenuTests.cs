namespace Slopsoil.Tests.Commands;

/// <summary>Spec 021: <c>!playlist add/remove/order</c>, and the menu and prompts under <c>!playlist</c>.</summary>
public sealed class MediaPlaylistMenuTests
{
    private readonly MediaHarness _h = new();

    public MediaPlaylistMenuTests()
    {
        foreach (var n in new[] { "a", "b", "c", "d" })
        {
            _h.Library.Search[n] = [MediaHarness.Movie(n, n.ToUpperInvariant(), null)];
        }
    }

    private async Task PlayWithQueue(params string[] queued)
    {
        await _h.Send("!play a");
        foreach (var q in queued)
        {
            await _h.Send($"!playlist add {q}");
        }

        _h.Sender.Sent.Clear();
    }

    private string Menu => _h.Texts.First(t => t.Contains(MediaHarness.MenuMarker, StringComparison.Ordinal));

    [Fact]
    public async Task The_menu_offers_only_what_can_be_done()
    {
        await _h.ShowPlaylist();
        Assert.EndsWith("**Change it?** Reply with:\n`add` — add something to the end", Menu, StringComparison.Ordinal);
        Assert.StartsWith("📭 Nothing on air and nothing on the playlist.", Menu, StringComparison.Ordinal);

        _h.Sender.Sent.Clear();
        await PlayWithQueue("b");
        await _h.ShowPlaylist();
        Assert.EndsWith("`add` — add something to the end\n`remove` — take something off", Menu, StringComparison.Ordinal);

        await PlayWithQueue("c");
        await _h.ShowPlaylist();
        Assert.EndsWith("`remove` — take something off\n`order` — move something up or down", Menu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Menu_add_asks_for_a_title_then_queues_it()
    {
        await PlayWithQueue();
        await _h.ShowPlaylist();
        await _h.Answer("ADD");
        await _h.Until(() => _h.Texts[^1].StartsWith("➕ **What should we add?**", StringComparison.Ordinal), "the title prompt");
        await _h.Answer("b");
        await _h.UntilReply("➕ Added to the playlist: **B** (#1)");
    }

    [Fact]
    public async Task Menu_remove_asks_which_without_listing_again()
    {
        await PlayWithQueue("b", "c");
        await _h.ShowPlaylist();
        await _h.Answer("remove");
        await _h.UntilReply("➖ **Which one comes off?** Reply with its number or name.");
        await _h.Answer("2");
        await _h.UntilReply("➖ Took **C** off the playlist.");
    }

    [Fact]
    public async Task Menu_order_asks_which_and_where()
    {
        await PlayWithQueue("b", "c", "d");
        await _h.ShowPlaylist();
        await _h.Answer("order");
        await _h.Until(() => _h.Texts[^1].StartsWith("↕️ **Which one moves, and where to?**", StringComparison.Ordinal), "the order prompt");
        await _h.Answer("3 1");
        await _h.UntilReply("↕️ Moved **D** to #1.");
    }

    [Fact]
    public async Task The_menu_ignores_chat_and_ends_quietly_on_a_command_or_timeout()
    {
        await PlayWithQueue("b");
        await _h.ShowPlaylist();
        await _h.Send("nice film");                 // chat that names no action: still waiting
        Assert.Equal(1, _h.Hub.WaiterCount);
        await _h.Send("!ping");                     // a new command ends it without a "cancelled"
        await _h.Until(() => _h.Hub.WaiterCount == 0, "the menu to end");
        Assert.DoesNotContain(_h.Texts, t => t.Contains("ancelled", StringComparison.Ordinal));

        await _h.ShowPlaylist();
        _h.Time.Advance(TimeSpan.FromSeconds(31));
        await _h.Until(() => _h.Hub.WaiterCount == 0, "the menu to time out");
        Assert.DoesNotContain(_h.Texts, t => t.Contains("ancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bare_remove_and_order_list_the_playlist_and_ask()
    {
        await PlayWithQueue("b", "c");
        _ = _h.Start("!playlist remove");
        await _h.UntilReply("📜 **Coming up** (2/10):\n  `1` B\n  `2` C\n➖ **Which one comes off?** Reply with its number or name.");
        await _h.Answer("b");
        await _h.UntilReply("➖ Took **B** off the playlist.");

        await _h.Send("!playlist add d");
        _ = _h.Start("!media playlist order");
        await _h.Until(() => _h.Texts[^1].StartsWith("📜 **Coming up** (2/10):\n  `1` C\n  `2` D\n↕️ **Which one moves", StringComparison.Ordinal), "the order prompt");
        await _h.Answer("d 1");
        await _h.UntilReply("↕️ Moved **D** to #1.");
    }

    [Fact]
    public async Task Bare_remove_and_order_say_when_there_is_nothing_to_do()
    {
        await _h.Send("!playlist remove");
        await _h.Send("!playlist order");
        Assert.Equal(["📭 The playlist is empty.", "📭 The playlist is empty."], _h.Texts);
        await PlayWithQueue("b");
        await _h.Send("!playlist order");
        Assert.Equal(["🙅 There's only one thing on the playlist — nothing to reorder."], _h.Texts);
    }

    [Fact]
    public async Task Prompts_cancel_on_a_command_or_timeout()
    {
        await PlayWithQueue("b");
        _ = _h.Start("!playlist add");
        await _h.Until(() => _h.Hub.WaiterCount > 0, "the title prompt");
        await _h.Send("!ping");
        await _h.UntilReply("Cancelled.");

        _ = _h.Start("!playlist remove");
        await _h.Until(() => _h.Hub.WaiterCount > 0, "the remove prompt");
        _h.Time.Advance(TimeSpan.FromSeconds(31));
        await _h.UntilReply("⌛ No answer — cancelled.");

        _ = _h.Start("!playlist order 1 1");
        await _h.UntilReply("↕️ Moved **B** to #1.");
    }
}

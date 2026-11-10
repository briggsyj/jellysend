using JellySend.Streaming.Engine;

namespace JellySend.Tests.Commands;

/// <summary>Spec 025: a command's replies, and the stream messages it causes, go to the chat it was typed in.</summary>
public sealed class ReplyChannelTests
{
    private readonly MediaHarness _h = new();

    public ReplyChannelTests()
    {
        _h.Library.Search["a"] = [MediaHarness.Movie("a", "A", null)];
        _h.Library.Search["b"] = [MediaHarness.Movie("b", "B", null)];
    }

    private IEnumerable<string> In(ulong channel)
    {
        lock (_h.Sender.Sent)
        {
            return [.. _h.Sender.Sent.Where(s => s.Channel == channel).Select(s => s.Content)];
        }
    }

    [Fact]
    public async Task A_restart_from_another_chat_announces_there()
    {
        await _h.Send("!play a", channel: 7);
        _h.Sender.Sent.Clear();
        await _h.Send("!res 480p", channel: 8);       // restarts the stream
        await _h.Until(() => In(8).Any(t => t.StartsWith("📺 Now showing", StringComparison.Ordinal)), "now showing in chat 8");
        Assert.Contains("📡 Tuning in to **A**…", In(8));
        Assert.Empty(In(7));
    }

    [Fact]
    public async Task Play_from_another_chat_announces_there()
    {
        await _h.Send("!play a", channel: 7);
        _h.Sender.Sent.Clear();
        await _h.Send("!play b", channel: 8);
        Assert.Equal(["📡 Tuning in to **B**…", "📺 Now showing: **B**"], In(8));
        Assert.Empty(In(7));
    }

    [Fact]
    public async Task Auto_advance_speaks_where_the_last_command_was()
    {
        await _h.Send("!play a", channel: 7);
        await _h.Send("!playlist add b", channel: 8);
        _h.Sender.Sent.Clear();
        _h.Current.End(PipelineEndReason.Natural);
        await _h.Until(() => In(8).Any(t => t.StartsWith("📺 Now showing: **B**", StringComparison.Ordinal)), "now showing in chat 8");
        Assert.Contains("⏭ Coming up next: **B**", In(8));
        Assert.Empty(In(7));
    }
}

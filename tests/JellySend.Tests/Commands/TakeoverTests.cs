namespace JellySend.Tests.Commands;

/// <summary>Spec 041: starting a stream while the bot streams in another server asks first.</summary>
public sealed class TakeoverTests
{
    private readonly MediaHarness _h = new();

    public TakeoverTests()
    {
        _h.Library.Search["a"] = [MediaHarness.Movie("a", "A", null)];
        _h.Library.Search["b"] = [MediaHarness.Movie("b", "B", null)];
        _h.Gateway.States[(6, 20)] = 60;                              // the author is also in voice in server 6
    }

    private async Task<Task> AskInServer6()
    {
        await _h.Send("!play a");                                    // streaming in server 5 (Movie Night)
        var run = Task.Run(() => _h.Send("!play b", guild: 6));
        await _h.Until(() => _h.Hub.WaiterCount > 0, "the question");
        return run;
    }

    [Fact]
    public async Task Yes_stops_the_other_stream_and_starts_here()
    {
        var run = await AskInServer6();
        Assert.Equal(
            "⚠️ The bot is streaming **A** in **Movie Night** right now, and can only be in one call. Starting here stops that stream. Reply `yes` to go ahead, or `no`.",
            _h.Texts[^1]);
        await _h.Send("YES", guild: 6);
        await run;
        Assert.Equal("B", _h.Current.Request.Title);
        Assert.Null(_h.Voice.Get(5));                                 // left server 5's call
        Assert.DoesNotContain(5UL, _h.Engine.ActiveGuilds);
        Assert.Contains("📴 Someone started a stream in another server, so this one has stopped.", _h.Texts);
    }

    [Fact]
    public async Task No_leaves_it_as_it_is()
    {
        var run = await AskInServer6();
        await _h.Send("no", guild: 6);
        await run;
        Assert.Equal("👍 Left as it is — nothing started here.", _h.Texts[^1]);
        Assert.Equal("A", _h.Current.Request.Title);
        Assert.Single(_h.Pipelines.Created);
        Assert.NotNull(_h.Voice.Get(5));
    }

    [Fact]
    public async Task No_answer_leaves_it_as_it_is()
    {
        var run = await AskInServer6();
        _h.Time.Advance(TimeSpan.FromSeconds(31));
        await run;
        Assert.Equal("⌛ No answer — nothing started here.", _h.Texts[^1]);
        Assert.Single(_h.Pipelines.Created);
    }

    [Fact]
    public async Task Playing_again_in_the_same_server_does_not_ask()
    {
        await _h.Send("!play a");
        await _h.Send("!play b");
        Assert.Equal("B", _h.Current.Request.Title);
        Assert.DoesNotContain(_h.Texts, t => t.StartsWith("⚠️", StringComparison.Ordinal));
    }
}

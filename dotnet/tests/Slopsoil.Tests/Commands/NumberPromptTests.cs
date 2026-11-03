using Slopsoil.Tests.Discord;
using Slopsoil.Common.Permissions;
using Slopsoil.Commands.Interaction;
using Slopsoil.Commands.Routing;

namespace Slopsoil.Tests.Commands;

public sealed class NumberPromptTests
{
    private readonly FakeSender _sender = new();
    private readonly ManualTimeProvider _time = new();
    private readonly MessageHub _hub;
    private readonly CommandRouter _router;
    private readonly TaskCompletionSource<int?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _others = [];

    public NumberPromptTests()
    {
        _hub = new MessageHub(_time);
        _router = new CommandRouter(new FakeRoles(), _sender, _hub);
        _router.Add("pick", Role.Viewer, async c => _result.SetResult(await NumberPrompt.AskAsync(c, 3)));
        _router.Add("other", Role.Viewer, c =>
        {
            _others.Add(c.Rest);
            return Task.CompletedTask;
        });
    }

    private Task Send(string content, ulong author = 20, ulong channel = 7) =>
        _router.DispatchAsync(new ChatMessage(5, channel, author, content), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("2", 2)]
    [InlineData(" 3 ", 3)]
    [InlineData("1", 1)]
    public async Task Valid_number_is_returned(string reply, int expected)
    {
        var pick = Send("!pick");
        await Send(reply);
        await pick;
        Assert.Equal(expected, await _result.Task);
        Assert.Empty(_sender.Sent);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("99999999999999")]
    public async Task Out_of_range_replies_and_cancels(string reply)
    {
        var pick = Send("!pick");
        await Send(reply);
        await pick;
        Assert.Null(await _result.Task);
        Assert.Equal(["invalid selection — pick a number between 1 and 3"], _sender.Texts);
    }

    [Fact]
    public async Task Other_users_channels_and_text_are_ignored_then_timeout_cancels()
    {
        var pick = Send("!pick");
        await Send("2", author: 30);
        await Send("2", channel: 8);
        await Send("two");
        await Send("-1");
        Assert.False(_result.Task.IsCompleted);

        _time.Advance(NumberPrompt.Timeout);
        await pick;
        Assert.Null(await _result.Task);
        Assert.Equal(["timed out — cancelled"], _sender.Texts);
    }

    [Fact]
    public async Task A_new_command_cancels_but_still_runs()
    {
        var pick = Send("!pick");
        await Send("!other thing");
        await pick;
        Assert.Null(await _result.Task);
        Assert.Equal(["cancelled"], _sender.Texts);
        Assert.Equal(["thing"], _others);
    }

    [Fact]
    public async Task Waiters_complete_once_and_are_removed()
    {
        var hub = new MessageHub(_time);
        var first = hub.WaitForAsync(m => m.Content == "x", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = hub.WaitForAsync(m => m.Content == "x", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var message = new ChatMessage(null, 1, 2, "x");
        hub.Publish(message);
        hub.Publish(message);
        Assert.Same(message, await first);
        Assert.Same(message, await second);

        using var cts = new CancellationTokenSource();
        var cancelled = hub.WaitForAsync(_ => true, TimeSpan.FromSeconds(5), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }
}

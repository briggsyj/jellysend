using JellySend.Commands.Interaction;
using JellySend.Commands.Routing;
using JellySend.Common.Permissions;
using JellySend.Tests.Discord;

namespace JellySend.Tests.Commands;

/// <summary>Spec 035: "typing…" while a command is busy.</summary>
public sealed class TypingIndicatorTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly TypingSender _sender = new();
    private readonly CommandRouter _router;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TypingIndicatorTests()
    {
        _router = new CommandRouter(new FakeRoles(), _sender, new MessageHub(_time));
        _router.Add("quick", Role.Viewer, c => c.ReplyAsync("done"));
        _router.Add("slow", Role.Viewer, async c =>
        {
            await _release.Task;
            await c.ReplyAsync("done");
        });
        _router.Add("ask", Role.Viewer, async c =>
        {
            await c.ReplyAsync("pick one");
            await NumberPrompt.AskAsync(c, 3);
            await _release.Task;
        });
    }

    private Task Send(string content) =>
        _router.DispatchAsync(new ChatMessage(5, 7, 20, content), TestContext.Current.CancellationToken);

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task A_quick_command_never_shows_typing()
    {
        await Send("!quick");
        Assert.Empty(_sender.Typing);
        Assert.Equal(["done"], _sender.Texts);
    }

    [Fact]
    public async Task A_busy_command_shows_typing_and_renews_it_until_done()
    {
        var run = Send("!slow");
        await Until(() => _time.ActiveCount == 1);
        _time.Advance(CommandRouter.TypingAfter);
        await Until(() => _sender.Typing.Count == 1 && _time.ActiveCount == 1);
        Assert.Equal(7UL, _sender.Typing[0]);
        _time.Advance(CommandRouter.TypingEvery);
        await Until(() => _sender.Typing.Count == 2);
        _release.SetResult();
        await run;
        _time.Advance(CommandRouter.TypingEvery * 3);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.Equal(2, _sender.Typing.Count);                          // stopped with the command
    }

    [Fact]
    public async Task No_typing_while_waiting_for_an_answer()
    {
        var run = Send("!ask");
        await Until(() => _router.Hub.WaiterCount > 0 && _time.ActiveCount == 2);   // typing delay + the prompt's 30 s
        _time.Advance(CommandRouter.TypingAfter);
        await Until(() => _time.ActiveCount == 2);                                  // skipped, renewal scheduled
        _time.Advance(CommandRouter.TypingEvery);
        await Until(() => _time.ActiveCount == 2);
        Assert.Empty(_sender.Typing);
        await Send("2");                                                            // answered: busy again
        await Until(() => _router.Hub.WaiterCount == 0 && _time.ActiveCount == 1);
        _time.Advance(CommandRouter.TypingEvery);
        await Until(() => _sender.Typing.Count == 1);
        _release.SetResult();
        await run;
    }

    [Fact]
    public async Task A_typing_failure_stops_trying_but_the_command_carries_on()
    {
        var errors = new List<string>();
        var sender = new TypingSender { Fail = true };
        var router = new CommandRouter(new FakeRoles(), sender, new MessageHub(_time), (what, _) => errors.Add(what));
        router.Add("slow", Role.Viewer, async c =>
        {
            await _release.Task;
            await c.ReplyAsync("done");
        });
        var run = router.DispatchAsync(new ChatMessage(5, 7, 20, "!slow"), TestContext.Current.CancellationToken);
        await Until(() => _time.ActiveCount == 1);
        _time.Advance(CommandRouter.TypingAfter);
        await Until(() => errors.Count == 1);
        _release.SetResult();
        await run;
        Assert.Equal(["typing"], errors);
        Assert.Equal(["done"], sender.Texts);
    }

    private sealed class TypingSender : IMessageSender, ITypingIndicator
    {
        public List<(ulong Channel, string Content)> Sent { get; } = [];

        public List<ulong> Typing { get; } = [];

        public bool Fail { get; init; }

        public IEnumerable<string> Texts => Sent.Select(s => s.Content);

        public Task SendAsync(ulong channelId, string content, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add((channelId, content));
            }

            return Task.CompletedTask;
        }

        public Task TypingAsync(ulong channelId, CancellationToken ct)
        {
            if (Fail)
            {
                throw new InvalidOperationException("403");
            }

            lock (Typing)
            {
                Typing.Add(channelId);
            }

            return Task.CompletedTask;
        }
    }
}

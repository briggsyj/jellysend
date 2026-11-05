using JellySend.Common.Permissions;
using JellySend.Commands.Routing;

namespace JellySend.Tests.Commands;

public sealed class RouterTests
{
    private readonly FakeSender _sender = new();
    private readonly List<string> _calls = [];
    private readonly List<string> _errors = [];
    private readonly CommandRouter _router;

    public RouterTests()
    {
        _router = new CommandRouter(new FakeRoles(), _sender, new MessageHub(), (name, ex) => _errors.Add($"{name}: {ex.Message}"));
        _router.Add("media", Role.Friend, Record("media"), "m")
            .Sub("pause", Role.Friend, Record("pause"))
            .Sub("aspect-ratio", Role.Friend, Record("aspect"), "aspect", "ar")
            .Sub("admin", Role.Admin, Record("admin"));
        _router.Add("ping", Role.Viewer, c => c.ReplyAsync("pong!"));
        _router.Add("boom", Role.Viewer, _ => throw new InvalidOperationException("kaput"));
        _router.Add("slow", Role.Viewer, c => Task.Delay(Timeout.Infinite, c.Cancellation));
    }

    private CommandHandler Record(string name) => c =>
    {
        _calls.Add($"{name}|{c.Rest}|{c.GuildId}|{c.ChannelId}|{c.AuthorId}");
        return Task.CompletedTask;
    };

    private Task Send(string content, ulong author = 20) =>
        _router.DispatchAsync(new ChatMessage(5, 7, author, content), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("!media The Sample", "media|The Sample")]
    [InlineData("  !media   Space Show  s01e02 ", "media|Space Show  s01e02")]
    [InlineData("!m pause", "pause|")]
    [InlineData("!media pause now please", "pause|now please")]
    [InlineData("!media ar 2", "aspect|2")]
    [InlineData("!media aspect-ratio", "aspect|")]
    [InlineData("!media", "media|")]
    [InlineData("!media Pause", "pause|")] // names are case-insensitive (casual users, phone auto-capitals)
    [InlineData("!MEDIA ar 2", "aspect|2")]
    public async Task Groups_route_to_subcommands_or_the_group_handler(string content, string expected)
    {
        await Send(content);
        Assert.Equal($"{expected}|5|7|20", Assert.Single(_calls));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("!")]
    [InlineData("!unknown thing")]
    [InlineData("media !x")]
    public async Task Non_commands_are_ignored(string content)
    {
        await Send(content);
        Assert.Empty(_calls);
        Assert.Empty(_sender.Sent);
    }

    [Theory]
    [InlineData(10UL, true)]
    [InlineData(20UL, true)]
    [InlineData(30UL, false)]
    [InlineData(40UL, false)]
    [InlineData(1UL, true)]
    public async Task Failed_role_checks_say_no(ulong author, bool runs)
    {
        await Send("!media x", author);
        Assert.Equal(runs, _calls.Count == 1);
        Assert.Equal(runs ? [] : [(7UL, CommandRouter.DeniedReply)], _sender.Sent);
        // The GIF link alone, so Discord unfurls and plays it.
        Assert.Matches(@"^https://media\d*\.giphy\.com/\S+\.gif$", CommandRouter.DeniedReply);
    }

    [Fact]
    public async Task Unknown_commands_stay_silent_for_everyone()
    {
        await Send("!nope", 40);
        await Send("!nope", 30);
        await Send("not a command", 40);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task A_failing_denied_reply_is_reported_not_thrown()
    {
        var router = new CommandRouter(new FakeRoles(), new ThrowingSender(), new MessageHub(), (name, ex) => _errors.Add($"{name}: {ex.Message}"));
        router.Add("x", Role.Admin, Record("x"));
        await router.DispatchAsync(new ChatMessage(5, 7, 30, "!x"), TestContext.Current.CancellationToken);
        Assert.Equal(["denied reply: send failed"], _errors);
    }

    private sealed class ThrowingSender : IMessageSender
    {
        public Task SendAsync(ulong channelId, string content, CancellationToken ct) => throw new InvalidOperationException("send failed");
    }

    [Fact]
    public async Task A_groups_role_gates_only_its_own_handler()
    {
        _router.Add("grp", Role.Friend, Record("grp")).Sub("open", Role.Viewer, Record("open"));
        await Send("!grp open now", 30);   // viewer: the open sub-command runs
        await Send("!grp something", 30);  // viewer: the friend-only group handler says no
        Assert.Equal(["open|now|5|7|30"], _calls);
        Assert.Equal([CommandRouter.DeniedReply], _sender.Texts);
    }

    [Fact]
    public async Task Subcommand_role_is_checked_too()
    {
        await Send("!media admin", 20);
        Assert.Empty(_calls);
        Assert.Equal([CommandRouter.DeniedReply], _sender.Texts);
        await Send("!media admin", 10);
        Assert.Single(_calls);
    }

    [Fact]
    public async Task Strangers_fail_the_global_check_even_for_viewer_commands()
    {
        await Send("!ping", 40);
        Assert.Equal([CommandRouter.DeniedReply], _sender.Texts);
        await Send("!ping", 30);
        Assert.Equal([CommandRouter.DeniedReply, "pong!"], _sender.Texts);
    }

    [Fact]
    public async Task Handler_errors_are_reported_not_thrown_and_cancellation_is_quiet()
    {
        await Send("!boom");
        Assert.Equal(["boom: kaput"], _errors);

        using var cts = new CancellationTokenSource();
        var slow = _router.DispatchAsync(new ChatMessage(5, 7, 20, "!slow"), cts.Token);
        await cts.CancelAsync();
        await slow;
        Assert.Single(_errors);
    }

    [Fact]
    public void Duplicate_names_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => _router.Add("ping", Role.Viewer, _ => Task.CompletedTask));
        Assert.Throws<ArgumentException>(() => _router.Add("p2", Role.Viewer, _ => Task.CompletedTask, "m"));
        var group = _router.Add("g", Role.Viewer, _ => Task.CompletedTask).Sub("a", Role.Viewer, _ => Task.CompletedTask);
        Assert.Throws<ArgumentException>(() => group.Sub("a", Role.Viewer, _ => Task.CompletedTask));
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("one", "one", "")]
    [InlineData(" one  two three ", "one", "two three")]
    [InlineData("one\ttwo", "one", "two")]
    public void Split_first(string text, string first, string rest) =>
        Assert.Equal((first, rest), CommandRouter.SplitFirst(text));

    [Fact]
    public async Task Every_command_is_reported_before_role_checks()
    {
        var seen = new List<(ulong Author, string Resolved, string Text)>();
        _router.OnCommand = (m, resolved) => seen.Add((m.AuthorId, resolved, m.Content));
        _router.Add("echo", Role.Viewer, c => c.ReplyAsync("echo"));
        _router.AddSecret("knock knock", "echo", _ => "who's there");
        await Send("!echo");
        await Send("!nope");
        await Send("knock knock");
        await Send("just chatting");
        Assert.Equal([(20UL, "echo", "!echo"), (20UL, "unknown", "!nope"), (20UL, "secret → echo", "knock knock")], seen);
    }
}

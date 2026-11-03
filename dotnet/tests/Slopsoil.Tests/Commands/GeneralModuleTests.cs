using Slopsoil.Commands.General;
using Slopsoil.Commands.Routing;

namespace Slopsoil.Tests.Commands;

public sealed class GeneralModuleTests
{
    private readonly FakeSender _sender = new();
    private bool _jellyfin;
    private readonly CommandRouter _router;

    public GeneralModuleTests()
    {
        _router = new CommandRouter(new FakeRoles(), _sender, new MessageHub());
        GeneralModule.Register(_router, () => _jellyfin);
    }

    private Task Send(string content, ulong author = 30) => _router.DispatchAsync(new ChatMessage(5, 7, author, content), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Ping_and_hello()
    {
        await Send("!ping");
        await Send("!hello");          // viewer: not admin, silent
        await Send("!hello", author: 10);
        Assert.Equal(["pong!", "Hello, world!"], _sender.Texts);
    }

    [Fact]
    public async Task Help_lists_jellyfin_only_when_configured()
    {
        await Send("!help");
        _jellyfin = true;
        await Send("!help");
        Assert.DoesNotContain("!media", _sender.Sent[0].Content, StringComparison.Ordinal);
        Assert.StartsWith("**General**\n  `!ping` — check if the bot is alive", _sender.Sent[0].Content, StringComparison.Ordinal);
        Assert.EndsWith(
            "**Jellyfin**\n  `!media <title>` — search Jellyfin for a movie, series, or episode and select it for playback",
            _sender.Sent[1].Content, StringComparison.Ordinal);
    }
}

using Slopsoil.Commands.Routing;
using Slopsoil.Commands.Voice;

namespace Slopsoil.Tests.Commands;

public sealed class VoiceModuleTests
{
    private readonly FakeSender _sender = new();
    private readonly FakeVoiceGateway _gateway = new();
    private readonly FakeStreams _streams = new();
    private readonly VoiceManager _voice;
    private readonly VoiceModule _module;
    private readonly CommandRouter _router;

    public VoiceModuleTests()
    {
        _voice = new VoiceManager(_gateway);
        _module = new VoiceModule(_voice, _streams);
        _router = new CommandRouter(new FakeRoles(), _sender, new MessageHub());
        _module.Register(_router);
    }

    private Task Send(string content, ulong? guild = 5, ulong author = 20) =>
        _router.DispatchAsync(new ChatMessage(guild, 7, author, content), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Join_requires_the_author_in_voice()
    {
        await Send("!join");
        Assert.Equal(["you're not in a voice channel"], _sender.Texts);
        Assert.Empty(_gateway.Links);
    }

    [Fact]
    public async Task Join_connects_reuses_and_moves()
    {
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        await Send("!join");
        Assert.Single(_gateway.Links);

        _gateway.States[(5, 20)] = 51;
        await Send("!join");
        Assert.Equal(2, _gateway.Links.Count);
        Assert.Equal(1, _gateway.Links[0].Disconnects);
        Assert.Equal(51UL, _voice.Get(5)!.ChannelId);
        Assert.Equal(["joined!", "joined!", "joined!"], _sender.Texts);
    }

    [Fact]
    public async Task Join_from_a_dm_finds_the_authors_guild_and_replaces_stale_links()
    {
        _gateway.States[(6, 20)] = 60;
        await Send("!join", guild: null);
        Assert.Equal((6UL, 60UL), (_gateway.Links[0].GuildId, _gateway.Links[0].ChannelId));

        _gateway.Links[0].IsConnected = false;
        _gateway.Links[0].ThrowOnDisconnect = true;
        await Send("!join", guild: null);
        Assert.Equal(2, _gateway.Links.Count);
        Assert.True(_gateway.Links[1].IsConnected);
    }

    [Fact]
    public async Task Leave_stops_streams_then_leaves()
    {
        await Send("!leave");
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        _streams.Streaming.Add(5);
        await Send("!leave");
        Assert.Equal(["not in a voice channel", "joined!", "left!"], _sender.Texts);
        Assert.Equal([(5UL, "leave")], _streams.Stops);
        Assert.False(_gateway.Links[0].IsConnected);
        Assert.Null(_voice.Get(5));
        Assert.False(await _voice.LeaveAsync(5));
    }

    [Fact]
    public async Task Leave_from_a_dm_uses_any_connected_guild()
    {
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        await Send("!leave", guild: null);
        Assert.Equal("left!", _sender.Texts.Last());
    }

    [Fact]
    public async Task Stop_needs_voice_and_a_stream_and_keeps_voice()
    {
        await Send("!stop");
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        await Send("!stop");
        _streams.Streaming.Add(5);
        await Send("!stop");
        Assert.Equal(["nothing is playing", "joined!", "nothing is playing", "stopped"], _sender.Texts);
        Assert.Equal([(5UL, "stop")], _streams.Stops);
        Assert.NotNull(_voice.Get(5));
    }

    [Fact]
    public async Task Auto_leave_when_the_last_human_leaves()
    {
        _gateway.States[(5, 20)] = 50;
        _gateway.States[(5, 30)] = 50;
        await Send("!join");

        await _module.OnVoiceStateUpdatedAsync(9, 20, null);         // other guild: no link
        await _module.OnVoiceStateUpdatedAsync(5, 1, 99);            // the bot itself
        await _module.OnVoiceStateUpdatedAsync(5, 20, 50);           // still in our channel (mute etc.)
        _gateway.States.Remove((5, 20));
        await _module.OnVoiceStateUpdatedAsync(5, 20, null);         // 30 still there
        Assert.Empty(_streams.Stops);

        _gateway.States[(5, 30)] = 51;
        await _module.OnVoiceStateUpdatedAsync(5, 30, 51);           // moved away: only the bot remains
        Assert.Equal([(5UL, "auto-leave")], _streams.Stops);
        Assert.Null(_voice.Get(5));
    }
}

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
        _router.Add("join", Slopsoil.Common.Permissions.Role.Friend, _module.JoinAsync);
        _router.Add("leave", Slopsoil.Common.Permissions.Role.Friend, _module.LeaveAsync);
        _router.Add("stop", Slopsoil.Common.Permissions.Role.Friend, _module.StopAsync);
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
        Assert.Equal(["not in a voice channel", "joined!", VoiceModule.LeftReply], _sender.Texts);
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
        Assert.Equal(VoiceModule.LeftReply, _sender.Texts.Last());
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
        Assert.Equal(["nothing is playing", "joined!", "nothing is playing", VoiceModule.StoppedReply], _sender.Texts);
        Assert.Equal([(5UL, "stop")], _streams.Stops);
        Assert.NotNull(_voice.Get(5));
    }

    [Fact]
    public async Task Auto_leave_when_the_last_human_leaves()
    {
        _gateway.States[(5, 20)] = 50;
        _gateway.States[(5, 30)] = 50;
        await Send("!join");

        await _module.OnVoiceStateChangedAsync(9, 20, 50, null);     // other guild: no link
        _gateway.States.Remove((5, 20));
        await _module.OnVoiceStateChangedAsync(5, 20, 50, null);     // 30 still there
        Assert.Empty(_streams.Stops);

        _gateway.States[(5, 30)] = 51;
        await _module.OnVoiceStateChangedAsync(5, 30, 50, 51);       // moved away: only the bot remains
        Assert.Equal([(5UL, "auto-leave")], _streams.Stops);
        Assert.Null(_voice.Get(5));
    }

    private (VoiceModule Module, Slopsoil.Tests.Discord.ManualTimeProvider Time) WithTimeout(double minutes)
    {
        var time = new Slopsoil.Tests.Discord.ManualTimeProvider();
        return (new VoiceModule(_voice, _streams, time: time, emptyChannelTimeout: TimeSpan.FromMinutes(minutes)), time);
    }

    [Fact]
    public async Task Empty_channel_keeps_streaming_until_the_timeout_then_stops_and_leaves()
    {
        var (module, time) = WithTimeout(20);
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        _streams.Streaming.Add(5);
        _gateway.States.Remove((5, 20));
        await module.OnVoiceStateChangedAsync(5, 20, 50, null);       // last viewer left
        Assert.Empty(_streams.Stops);
        time.Advance(TimeSpan.FromMinutes(19));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Empty(_streams.Stops);
        time.Advance(TimeSpan.FromMinutes(1));
        await module.EmptyCountdowns[5];
        Assert.Equal([(5UL, "empty-timeout")], _streams.Stops);
        Assert.Null(_voice.Find(5));
    }

    [Fact]
    public async Task Rejoining_cancels_and_leaving_again_restarts_the_countdown()
    {
        var (module, time) = WithTimeout(20);
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        _gateway.States.Remove((5, 20));
        await module.OnVoiceStateChangedAsync(5, 20, 50, null);
        var first = module.EmptyCountdowns[5];
        time.Advance(TimeSpan.FromMinutes(10));
        _gateway.States[(5, 30)] = 50;
        await module.OnVoiceStateChangedAsync(5, 30, null, 50);       // someone came back
        await first;
        time.Advance(TimeSpan.FromMinutes(15));                        // past the original deadline
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Empty(_streams.Stops);

        _gateway.States.Remove((5, 30));
        await module.OnVoiceStateChangedAsync(5, 30, 50, null);       // empty again: a fresh 20 minutes
        time.Advance(TimeSpan.FromMinutes(19));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Empty(_streams.Stops);
        time.Advance(TimeSpan.FromMinutes(1));
        await module.EmptyCountdowns[5];
        Assert.Single(_streams.Stops);
    }

    [Fact]
    public async Task Countdown_ends_quietly_when_the_bot_already_left_or_people_came_back_unseen()
    {
        var (module, time) = WithTimeout(1);
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        _gateway.States.Remove((5, 20));
        await module.OnVoiceStateChangedAsync(5, 20, 50, null);
        _gateway.States[(5, 40)] = 50;                                 // a viewer the event stream missed
        time.Advance(TimeSpan.FromMinutes(1));
        await module.EmptyCountdowns[5];
        Assert.Empty(_streams.Stops);

        _gateway.States.Remove((5, 40));
        await module.OnVoiceStateChangedAsync(5, 40, 50, null);
        var countdown = module.EmptyCountdowns[5];
        await _voice.LeaveAsync(5);                                    // !leave meanwhile
        var selfLeft = module.OnVoiceStateChangedAsync(5, 1, 50, null); // the bot's own leave cancels it
        await countdown;                                               // cancelled, without waiting for time
        time.Advance(TimeSpan.FromMinutes(1));                         // (also passes the self-left grace)
        await selfLeft;
        Assert.Empty(_streams.Stops);
    }

    [Fact]
    public async Task Moved_into_an_empty_channel_starts_the_countdown()
    {
        var (module, time) = WithTimeout(5);
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        _gateway.States[(5, 1)] = 77;                                  // a moderator moved the bot
        await module.OnVoiceStateChangedAsync(5, 1, 50, 77);
        time.Advance(TimeSpan.FromMinutes(5));
        await module.EmptyCountdowns[5];
        Assert.Equal([(5UL, "empty-timeout")], _streams.Stops);
    }

    [Fact]
    public async Task Zero_timeout_leaves_at_once_and_a_move_into_an_empty_channel_does_too()
    {
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        _gateway.States[(5, 1)] = 77;
        await _module.OnVoiceStateChangedAsync(5, 1, 50, 77);
        Assert.Equal([(5UL, "auto-leave")], _streams.Stops);
    }

    [Fact]
    public async Task Events_outside_our_channel_never_auto_leave()
    {
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        _gateway.States.Remove((5, 20));                             // cache incomplete: looks empty
        await _module.OnVoiceStateChangedAsync(5, 40, 60, 61);       // someone moving between other channels
        await _module.OnVoiceStateChangedAsync(5, 40, null, 60);     // someone joining another channel
        Assert.Empty(_streams.Stops);
    }

    [Fact]
    public async Task Kicked_or_dropped_bot_discards_the_session_after_a_grace_period()
    {
        var time = new Slopsoil.Tests.Discord.ManualTimeProvider();
        var module = new VoiceModule(_voice, _streams, time: time);
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        _streams.Streaming.Add(5);
        _gateway.Links[0].IsConnected = false;
        _gateway.States.Remove((5, 1));                              // the bot is no longer in voice

        var left = module.OnVoiceStateChangedAsync(5, 1, 50, null);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Empty(_streams.Stops);
        time.Advance(VoiceModule.SelfLeftGrace);
        await left;
        Assert.Equal([(5UL, "disconnected")], _streams.Stops);
        Assert.Null(_voice.Find(5));
    }

    [Fact]
    public async Task Self_leave_followed_by_a_rejoin_or_during_a_start_is_ignored()
    {
        var time = new Slopsoil.Tests.Discord.ManualTimeProvider();
        var module = new VoiceModule(_voice, _streams, time: time);
        _gateway.States[(5, 20)] = 50;
        await Send("!join");                                         // bot is in (5, 50) again
        var moved = module.OnVoiceStateChangedAsync(5, 1, 49, null);
        time.Advance(VoiceModule.SelfLeftGrace);
        await moved;

        _gateway.States.Remove((5, 1));
        _streams.Starting.Add(5);
        var starting = module.OnVoiceStateChangedAsync(5, 1, 50, null);
        time.Advance(VoiceModule.SelfLeftGrace);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(starting.IsCompleted);                          // waits for the start instead of giving up
        _gateway.States[(5, 1)] = 50;                                // the start rejoined
        _streams.Starting.Clear();
        time.Advance(VoiceModule.SelfLeftGrace);
        await starting;

        _streams.Starting.Clear();
        await _voice.LeaveAsync(5);                                  // our own !leave already cleaned up
        var own = module.OnVoiceStateChangedAsync(5, 1, 50, null);
        time.Advance(VoiceModule.SelfLeftGrace);
        await own;
        Assert.Empty(_streams.Stops);
    }

    [Fact]
    public async Task Self_leave_during_a_start_that_ends_out_of_voice_tears_down()
    {
        var time = new Slopsoil.Tests.Discord.ManualTimeProvider();
        var module = new VoiceModule(_voice, _streams, time: time);
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        _gateway.States.Remove((5, 1));                              // kicked mid-start
        _streams.Starting.Add(5);
        var left = module.OnVoiceStateChangedAsync(5, 1, 50, null);
        time.Advance(VoiceModule.SelfLeftGrace);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        _streams.Starting.Clear();
        time.Advance(VoiceModule.SelfLeftGrace);
        await left;
        Assert.Equal([(5UL, "disconnected")], _streams.Stops);
    }

    [Fact]
    public async Task Leave_waits_for_an_in_flight_connect_and_removes_it()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new SlowGateway(_gateway, release.Task);
        var voice = new VoiceManager(slow);
        var connecting = voice.EnsureAsync(5, 50, TestContext.Current.CancellationToken);
        var leaving = voice.LeaveAsync(5);
        Assert.False(leaving.IsCompleted);
        release.SetResult();
        await connecting;
        await leaving;
        Assert.Null(voice.Find(5));
        Assert.False(_gateway.Links[0].IsConnected);
    }

    private sealed class SlowGateway(FakeVoiceGateway inner, Task gate) : IVoiceGateway
    {
        public ulong SelfId => inner.SelfId;

        public ulong? GetVoiceChannel(ulong guildId, ulong userId) => inner.GetVoiceChannel(guildId, userId);

        public (ulong Guild, ulong Channel)? FindUserVoice(ulong userId) => inner.FindUserVoice(userId);

        public IReadOnlyCollection<ulong> UsersInChannel(ulong guildId, ulong channelId) => inner.UsersInChannel(guildId, channelId);

        public async Task<IVoiceLink> ConnectAsync(ulong guildId, ulong channelId, CancellationToken ct)
        {
            await gate;
            return await inner.ConnectAsync(guildId, channelId, ct);
        }
    }

    [Fact]
    public async Task Leave_and_stop_work_with_a_stale_link()
    {
        _gateway.States[(5, 20)] = 50;
        await Send("!join");
        _streams.Streaming.Add(5);
        _gateway.Links[0].IsConnected = false;                       // kicked: link is stale
        await Send("!stop");
        _streams.Streaming.Add(5);
        await Send("!leave");
        Assert.Equal(["joined!", VoiceModule.StoppedReply, VoiceModule.LeftReply], _sender.Texts);
        Assert.Equal([(5UL, "stop"), (5UL, "leave")], _streams.Stops);
    }
}

using JellySend.Commands.Voice;

namespace JellySend.Tests.Commands;

/// <summary>Spec 023: asked from a server channel by someone not in voice, broadcast in the server's first voice channel.</summary>
public sealed class VoiceFallbackTests
{
    private readonly MediaHarness _h = new(emptyChannelTimeout: TimeSpan.FromMinutes(20));

    public VoiceFallbackTests()
    {
        _h.Gateway.States.Remove((5, 20));                  // the author is not in voice
        _h.Gateway.VoiceChannels[5] = [100, 101];
        _h.Library.Search["sample film"] = [MediaHarness.Movie("m1", "Sample Film")];
    }

    [Fact]
    public async Task Play_joins_the_first_voice_channel_streams_and_starts_the_countdown()
    {
        await _h.Send("!play sample film");
        Assert.Equal(100UL, _h.Gateway.Links[^1].ChannelId);
        Assert.Equal("Sample Film (2010)", _h.Current.Request.Title);
        Assert.Equal(_h.VoiceCommands.FallbackNotice, _h.Texts[0]);
        Assert.Contains("within 20 minutes", _h.Texts[0], StringComparison.Ordinal);
        Assert.True(_h.VoiceCommands.EmptyCountdowns.ContainsKey(5)); // nobody there yet

        _h.Time.Advance(TimeSpan.FromMinutes(20));
        await _h.VoiceCommands.EmptyCountdowns[5];
        Assert.False(_h.Media.IsStreaming(5));                       // nobody came: signed off
        Assert.Null(_h.Voice.Get(5));
        Assert.Equal(                                                // said in the voice channel's own chat
            (100UL, "📴 Nobody's been in the channel for 20 minutes — signing off. `!play` to start again."),
            _h.Sender.Sent[^1]);
    }

    [Fact]
    public async Task Someone_joining_cancels_the_countdown()
    {
        await _h.Send("!play sample film");
        _h.Gateway.States[(5, 30)] = 100;
        await _h.VoiceCommands.OnVoiceStateChangedAsync(5, 30, null, 100);
        _h.Time.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(_h.Media.IsStreaming(5));
    }

    [Fact]
    public async Task Join_does_the_same()
    {
        await _h.Send("!join");
        Assert.Equal([_h.VoiceCommands.FallbackNotice, "📡 Joined — ready to broadcast."], _h.Texts);
        Assert.Equal(100UL, _h.Gateway.Links[^1].ChannelId);
        Assert.True(_h.VoiceCommands.EmptyCountdowns.ContainsKey(5));
    }

    [Fact]
    public async Task When_the_bot_is_already_in_voice_it_stays_where_it_is()
    {
        await _h.Send("!play sample film");
        _h.Gateway.States[(5, 30)] = 101;                            // someone else, elsewhere
        await _h.Voice.EnsureAsync(5, 101, TestContext.Current.CancellationToken);
        _h.Sender.Sent.Clear();
        await _h.Send("!play sample film");
        Assert.Equal(101UL, _h.Gateway.Links[^1].ChannelId);
        Assert.DoesNotContain(_h.VoiceCommands.FallbackNotice, _h.Texts);
    }

    [Fact]
    public async Task The_authors_own_channel_still_wins()
    {
        _h.Gateway.States[(5, 20)] = 50;
        await _h.Send("!play sample film");
        Assert.Equal(50UL, _h.Gateway.Links[^1].ChannelId);
        Assert.DoesNotContain(_h.VoiceCommands.FallbackNotice, _h.Texts);
        Assert.False(_h.VoiceCommands.EmptyCountdowns.ContainsKey(5));
    }

    [Theory]
    [InlineData(true, false)]   // a DM: no server to pick a channel in
    [InlineData(false, false)]  // a server without voice channels
    [InlineData(false, true)]   // the channel list could not be fetched
    public async Task Nowhere_to_go_says_join_first(bool dm, bool error)
    {
        _h.Gateway.VoiceChannels.Clear();
        if (error)
        {
            _h.Gateway.VoiceChannelsError = new InvalidOperationException("403");
        }

        await _h.Send("!play sample film", guild: dm ? null : 5);
        Assert.Equal(["🙅 Join a voice channel first, then try again."], _h.Texts);
        Assert.Empty(_h.Gateway.Links);
    }

    [Fact]
    public void With_no_timeout_the_notice_promises_nothing_and_no_countdown_starts()
    {
        var voice = new VoiceModule(new VoiceManager(new FakeVoiceGateway()), new FakeStreams());
        Assert.DoesNotContain("sign off", voice.FallbackNotice, StringComparison.Ordinal);
        Assert.Contains("1 minute.", new VoiceModule(new VoiceManager(new FakeVoiceGateway()), new FakeStreams(), emptyChannelTimeout: TimeSpan.FromMinutes(1)).FallbackNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_notice_that_cannot_be_posted_does_not_stop_the_sign_off()
    {
        var gateway = new FakeVoiceGateway();
        gateway.VoiceChannels[5] = [100];
        var voice = new VoiceManager(gateway);
        var streams = new FakeStreams();
        var time = new JellySend.Tests.Discord.ManualTimeProvider();
        var module = new VoiceModule(voice, streams, time: time, emptyChannelTimeout: TimeSpan.FromMinutes(1), announcer: new ThrowingSender());
        await voice.EnsureAsync(5, 100, TestContext.Current.CancellationToken);
        await module.WatchIfEmptyAsync(5);
        time.Advance(TimeSpan.FromMinutes(1));
        await module.EmptyCountdowns[5];
        Assert.Contains((5UL, "empty-timeout"), streams.Stops);
        Assert.Null(voice.Get(5));
    }

    private sealed class ThrowingSender : JellySend.Commands.Routing.IMessageSender
    {
        public Task SendAsync(ulong channelId, string content, CancellationToken ct) => throw new InvalidOperationException("403");
    }
}

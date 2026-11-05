using JellySend.Commands.Interaction;
using JellySend.Commands.Media;
using JellySend.Commands.Routing;
using JellySend.Common.Permissions;
using JellySend.Services.Jellyfin;
using JellySend.Streaming.Engine;
using JellySend.Tests.Discord;

namespace JellySend.Tests.Commands;

/// <summary>Fixes from the pre-release review: input bounds, echoes, prompt ownership, and endings that say one thing.</summary>
public sealed class ReviewFixesTests
{
    private readonly MediaHarness _h = new();

    [Theory]
    [InlineData("show s1e1-99999999999")]
    [InlineData("show s1e1-s99999999999e1")]
    [InlineData("show s1e1-1000")]
    [InlineData("show 1x1-2x1000")]
    [InlineData("show s1e1,2,1000")]
    [InlineData("show season 1000 episodes 1 to 3")]
    public void Numbers_past_999_are_never_a_range(string text) => Assert.Null(PlayQuery.Parse(text).Spans);

    [Fact]
    public void Ranges_up_to_999_still_work() => Assert.Equal("S01E01–E999", PlayQuery.Parse("show s1e1-999").Spans!.Single().ToString());

    [Fact]
    public async Task A_huge_range_typo_finishes_quickly()
    {
        _h.Library.Search["show"] = [new MediaItem { Id = "s", Name = "Example Show", Type = "Series" }];
        var run = _h.Send("!play show s1e1-99999999999");
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_bots_own_messages_coming_back_are_ignored()
    {
        _h.Library.Search["b"] = [MediaHarness.Movie("b", "B", null)];
        var run = _h.Start("!playlist add");
        await _h.Until(() => _h.Hub.WaiterCount > 0, "the title prompt");
        var prompt = _h.Texts[^1];
        await _h.Send(prompt);                 // the self-bot's own prompt, echoed back by Discord
        Assert.Equal(1, _h.Hub.WaiterCount);   // still waiting for a person
        await _h.Send(prompt);                 // the same text again is no longer an echo: a person typed it
        await run;
        Assert.Equal(0, _h.Hub.WaiterCount);
    }

    [Fact]
    public async Task One_reply_answers_only_the_newest_prompt()
    {
        var hub = new MessageHub(new ManualTimeProvider());
        var older = hub.WaitForAsync(m => m.Content == "2", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var newer = hub.WaitForAsync(m => m.Content == "2", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        hub.Publish(new ChatMessage(5, 7, 20, "2"));
        Assert.NotNull(await newer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(older.IsCompleted);
        hub.Publish(new ChatMessage(5, 7, 20, "2"));
        Assert.NotNull(await older);
    }

    [Fact]
    public async Task The_echo_log_is_bounded()
    {
        var sender = new FakeSender();
        var router = new CommandRouter(new FakeRoles(), sender, new MessageHub(new ManualTimeProvider()));
        var runs = 0;
        router.Add("say", Role.Viewer, async c =>
        {
            runs++;
            for (var i = 0; i < 100; i++)
            {
                await c.ReplyAsync($"line {i}");
            }
        });
        await router.DispatchAsync(new ChatMessage(5, 7, 20, "!say"), TestContext.Current.CancellationToken);
        Assert.Equal(100, sender.Sent.Count);
        await router.DispatchAsync(new ChatMessage(5, 7, 20, "!say"), TestContext.Current.CancellationToken);
        Assert.Equal(2, runs); // "!say" was never sent by the bot, so it is no echo
    }

    [Fact]
    public async Task A_recovery_that_throws_says_so_once()
    {
        _h.Library.Direct = true;
        _h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000)];
        await _h.Send("!play film");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetException(new InvalidOperationException("boom"));
        _h.Library.PlaybackGate = gate;
        _h.Current.End(PipelineEndReason.Failed, FfmpegPipeline.FailedToStart);
        await _h.Until(() => !_h.Media.IsStarting(5) && _h.Texts.Count(t => t.StartsWith('❌')) >= 1 && _h.Library.Stopped.Contains("ps1"), "the failure");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(_h.Texts, t => t.StartsWith('❌'));
        Assert.Single(_h.Texts, t => t.StartsWith("😅", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_lost_signal_with_more_to_play_does_not_say_play_again()
    {
        _h.Library.Search["a"] = [MediaHarness.Movie("a", "A", null)];
        _h.Library.Search["b"] = [MediaHarness.Movie("b", "B", null)];
        await _h.Send("!play a");
        await _h.Send("!playlist add b");
        _h.Gateway.States.Remove((5, 1));      // kicked, so no reconnect
        _h.Connector.Links[^1].Close();
        await _h.UntilReply(MediaModule.LostSignalNext);
        Assert.DoesNotContain(StreamEngine.ConnectionLostMessage, _h.Texts);
    }

    [Theory]
    [InlineData("!help queue")]
    [InlineData("!help remove")]
    [InlineData("!help order")]
    public async Task Help_for_an_old_playlist_name_shows_playlist(string content)
    {
        await _h.Send(content);
        Assert.Equal([CommandCatalog.Find("playlist")!.HelpText()], _h.Texts);
    }

    [Fact]
    public async Task A_long_media_title_is_shortened_in_the_hint()
    {
        await _h.Send("!media " + new string('x', 3000));
        Assert.True(_h.Texts[0].Length <= 2000, $"{_h.Texts[0].Length} characters");
        Assert.Contains("x…`", _h.Texts[0], StringComparison.Ordinal);
    }
}

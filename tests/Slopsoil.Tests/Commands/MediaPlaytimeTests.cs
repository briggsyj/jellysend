using Slopsoil.Streaming.Engine;

namespace Slopsoil.Tests.Commands;

/// <summary>Spec 016: a stream stops after MAX_STREAM_HOURS (12 h), counted from the user's start.</summary>
public sealed class MediaPlaytimeTests
{
    private readonly MediaHarness _h = new();

    private const string Limit = "⏹ stopped after 12 hours of streaming — `!play` to start again (the playlist is kept)";

    public MediaPlaytimeTests()
    {
        _h.Library.Search["a"] = [MediaHarness.Movie("a", "A", 2000)];
        _h.Library.Search["b"] = [MediaHarness.Movie("b", "B", 2001)];
        _h.Library.Search["c"] = [MediaHarness.Movie("c", "C", 2002)];
    }

    [Fact]
    public async Task The_stream_stops_at_12_hours_and_keeps_the_playlist()
    {
        await _h.Send("!play a");
        await _h.Send("!queue b");
        _h.Time.Advance(TimeSpan.FromHours(11.9));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(_h.Media.IsStreaming(5));

        _h.Time.Advance(TimeSpan.FromHours(0.1));
        await _h.Media.PlaytimeLimits[5].Task;
        Assert.Equal(Limit, _h.Texts[^1]);
        Assert.False(_h.Media.IsStreaming(5));
        Assert.Equal(1, _h.Current.Stops);
        Assert.NotNull(_h.Voice.Get(5));                 // still in voice
        await _h.Send("!playlist");
        Assert.Contains("`1` B (2001)", _h.Texts[^1], StringComparison.Ordinal);
        await _h.Send("!play");                           // resume the playlist: a fresh 12 hours
        Assert.Equal("B (2001)", _h.Current.Request.Title);
        Assert.False(_h.Media.PlaytimeLimits[5].Task.IsCompleted);
    }

    [Fact]
    public async Task Auto_advance_keeps_counting_from_the_users_start()
    {
        await _h.Send("!play a");
        await _h.Send("!queue b");
        _h.Time.Advance(TimeSpan.FromHours(6));
        _h.Current.End(PipelineEndReason.Natural);        // A ends; B plays on its own
        await _h.Until(() => _h.Pipelines.Created.Count == 2, "auto-advance");
        _h.Time.Advance(TimeSpan.FromHours(6));
        await _h.Media.PlaytimeLimits[5].Task;
        Assert.Equal(Limit, _h.Texts[^1]);
        Assert.False(_h.Media.IsStreaming(5));
    }

    [Fact]
    public async Task A_new_play_restarts_the_count()
    {
        await _h.Send("!play a");
        var first = _h.Media.PlaytimeLimits[5].Task;
        _h.Time.Advance(TimeSpan.FromHours(10));
        await _h.Send("!play b");
        await first;                                      // the old countdown was cancelled
        _h.Time.Advance(TimeSpan.FromHours(10));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(_h.Media.IsStreaming(5));
        Assert.DoesNotContain(Limit, _h.Texts);
    }

    [Fact]
    public async Task Nothing_happens_when_the_stream_already_ended_or_the_bot_left()
    {
        await _h.Send("!play a");
        await _h.Send("!stop");
        _h.Time.Advance(TimeSpan.FromHours(12));
        await _h.Media.PlaytimeLimits[5].Task;
        Assert.DoesNotContain(Limit, _h.Texts);

        await _h.Send("!play a");
        await _h.Send("!leave");
        Assert.False(_h.Media.PlaytimeLimits.ContainsKey(5));
    }

    [Fact]
    public async Task Zero_means_no_limit()
    {
        var h = new MediaHarness(maxPlaytime: TimeSpan.Zero);
        h.Library.Search["a"] = [MediaHarness.Movie("a", "A", 2000)];
        await h.Send("!play a");
        Assert.Empty(h.Media.PlaytimeLimits);
    }
}

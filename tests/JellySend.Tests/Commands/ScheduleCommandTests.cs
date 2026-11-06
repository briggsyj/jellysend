namespace JellySend.Tests.Commands;

/// <summary>Spec 032: <c>!schedule</c> — start times from the time left in what's on, and the end.</summary>
public sealed class ScheduleCommandTests
{
    private readonly MediaHarness _h = new();

    public ScheduleCommandTests()
    {
        _h.Library.Search["a"] = [MediaHarness.Movie("a", "A", null, 90L * 60 * 10_000_000)];   // 90 min
        _h.Library.Search["b"] = [MediaHarness.Movie("b", "B", null, 60L * 60 * 10_000_000)];   // 60 min
        _h.Library.Search["c"] = [MediaHarness.Movie("c", "C", null, 30L * 60 * 10_000_000)];   // 30 min
        _h.Library.Search["n"] = [MediaHarness.Movie("n", "N", null, ticks: null)];             // unknown length
    }

    private long Now => _h.Time.GetUtcNow().ToUnixTimeSeconds();

    [Fact]
    public async Task Lists_start_times_from_the_time_left_and_the_end()
    {
        await _h.Send("!play a");
        await _h.Send("!playlist add b");
        await _h.Send("!playlist add c");
        _h.Current.Fake.Position = 30 * 60;                              // 60 min left of A
        await _h.Send("!schedule");
        var start = Now + 60 * 60;
        Assert.Equal(
            $"🗓️ **Schedule** (in your time zone):\n📺 Now: A\n`1` <t:{start}:t> B\n`2` <t:{start + 3600}:t> C\n🏁 Ends around <t:{start + 5400}:t> (<t:{start + 5400}:R>)",
            _h.Texts[^1]);
    }

    [Fact]
    public async Task With_nothing_queued_it_just_says_the_end()
    {
        await _h.Send("!play a");
        _h.Current.Fake.Position = 80 * 60;
        await _h.Send("!media schedule");
        Assert.Equal($"🗓️ **A** ends around <t:{Now + 600}:t> (<t:{Now + 600}:R>).", _h.Texts[^1]);
    }

    [Fact]
    public async Task Paused_says_the_times_assume_it_carries_on()
    {
        await _h.Send("!play a");
        await _h.Send("!pause");
        await _h.Send("!schedule");
        Assert.EndsWith("— paused, so times assume it carries on now.", _h.Texts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_running_time_stops_the_times_after_it()
    {
        await _h.Send("!play a");
        await _h.Send("!playlist add n");
        await _h.Send("!playlist add b");
        await _h.Send("!schedule");
        Assert.Contains($"`1` <t:{Now + 5400}:t> N", _h.Texts[^1], StringComparison.Ordinal);
        Assert.Contains("`2` — B", _h.Texts[^1], StringComparison.Ordinal);
        Assert.EndsWith("🏁 End time unknown — an item's running time isn't known.", _h.Texts[^1], StringComparison.Ordinal);

        await _h.Send("!play n");                                       // now playing N; the playlist still holds N and B
        await _h.Send("!playlist remove 1");
        await _h.Send("!playlist remove 1");
        await _h.Send("!schedule");
        Assert.Equal("🗓️ **N** — its running time isn't known, so neither is the end.", _h.Texts[^1]);
    }

    [Fact]
    public async Task A_waiting_playlist_is_timed_from_now()
    {
        await _h.Send("!play a");
        await _h.Send("!playlist add b");
        await _h.Send("!stop");
        await _h.Send("!schedule");
        Assert.Equal(
            $"🗓️ **Schedule** if the playlist starts now (`!play`), in your time zone:\n`1` <t:{Now}:t> B\n🏁 Ends around <t:{Now + 3600}:t> (<t:{Now + 3600}:R>)",
            _h.Texts[^1]);
    }

    [Fact]
    public async Task Nothing_at_all()
    {
        await _h.Send("!schedule");
        Assert.Equal(["📭 Nothing on air and nothing on the playlist."], _h.Texts);
    }
}

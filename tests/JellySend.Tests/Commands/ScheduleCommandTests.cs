using JellySend.Commands.Media;

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
        Assert.Equal(["📭 Nothing on air and nothing on the playlist.\n-# Upcoming events: `!schedule events`."], _h.Texts);
    }

    // ── Spec 036: !schedule events ───────────────────────────────────────

    private void Upcoming(params MediaModule.UpcomingScreening[] events) => _h.Media.UpcomingEvents = () => events;

    private MediaModule.UpcomingScreening Event(ulong id, ulong guild, string name, double inMinutes, params string[] lines) =>
        new(id, guild, name, _h.Time.GetUtcNow().AddMinutes(inMinutes), lines, 20, 300);

    [Fact]
    public async Task Events_are_listed_soonest_first_with_their_item_times()
    {
        _h.Library.Search["twin"] = [MediaHarness.Movie("t1", "Twin", 1990), MediaHarness.Movie("t2", "Twin", 2020)];
        var soon = _h.Time.GetUtcNow().AddMinutes(60).ToUnixTimeSeconds();
        var later = _h.Time.GetUtcNow().AddMinutes(600).ToUnixTimeSeconds();
        Upcoming(Event(2, 5, "Late Show", 600, "c"), Event(1, 5, "Movie Night", 60, "a", "b"), Event(3, 6, "Elsewhere", 30, "a"));
        await _h.Send("!schedule events");
        Assert.Equal(
            "📅 **Upcoming events** (in your time zone):\n\n"
            + $"🎬 **Movie Night** — <t:{soon}:f> (<t:{soon}:R>)\n`1` <t:{soon}:t> A\n`2` <t:{soon + 5400}:t> B\n🏁 Ends around <t:{soon + 9000}:t>\n\n"
            + $"🎬 **Late Show** — <t:{later}:f> (<t:{later}:R>)\n`1` <t:{later}:t> C\n🏁 Ends around <t:{later + 1800}:t>",
            _h.Texts[^1]);                                                // this server only: Elsewhere (server 6) is left out
    }

    [Fact]
    public async Task Open_titles_are_marked_and_stop_the_times()
    {
        _h.Library.Search["twin"] = [MediaHarness.Movie("t1", "Twin", 1990), MediaHarness.Movie("t2", "Twin", 2020)];
        Upcoming(Event(1, 5, "Movie Night", 60, "twin", "lost film", "a"));
        await _h.Send("!schedule upcoming");
        var start = _h.Time.GetUtcNow().AddMinutes(60).ToUnixTimeSeconds();
        Assert.Contains($"`1` <t:{start}:t> `twin` — ⏳ several matches, to be picked", _h.Texts[^1], StringComparison.Ordinal);
        Assert.Contains("`2` — `lost film` — 🔎 not found yet", _h.Texts[^1], StringComparison.Ordinal);
        Assert.Contains("`3` — A", _h.Texts[^1], StringComparison.Ordinal);
        Assert.EndsWith("🏁 End time to follow, once every title is settled.", _h.Texts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task From_a_dm_every_servers_events_show_with_the_server()
    {
        Upcoming(Event(1, 5, "Movie Night", 60, "a"), Event(3, 6, "Elsewhere", 30, "b"));
        await _h.Send("!schedule events", guild: null);
        Assert.Contains("🎬 **Elsewhere** — ", _h.Texts[^1], StringComparison.Ordinal);
        Assert.Contains("🎬 **Movie Night** · Movie Night — ", _h.Texts[^1], StringComparison.Ordinal);  // guild 5's name
        Assert.True(_h.Texts[^1].IndexOf("Elsewhere", StringComparison.Ordinal) < _h.Texts[^1].IndexOf("Movie Night", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_upcoming_events()
    {
        await _h.Send("!schedule events");
        Upcoming(Event(3, 6, "Elsewhere", 30, "a"));
        await _h.Send("!schedule events");
        Assert.All(_h.Texts, t => Assert.Equal("📭 No upcoming events with screenings. `!help events` shows how to set one up.", t));
    }

    [Fact]
    public async Task Many_events_are_split_across_messages_whole()
    {
        var events = Enumerable.Range(1, 30)
            .Select(i => Event((ulong)i, 5, $"Night {i}", i * 60, "a", "b", "c")).ToArray();
        Upcoming(events);
        await _h.Send("!schedule events");
        Assert.True(_h.Texts.Count > 1);
        Assert.All(_h.Texts, t => Assert.True(t.Length <= 2000, $"{t.Length} characters"));
        Assert.StartsWith("📅 **Upcoming events** (continued):", _h.Texts[1], StringComparison.Ordinal);
        Assert.Equal(30, _h.Texts.Sum(t => t.Split("🎬 ").Length - 1));
    }
}

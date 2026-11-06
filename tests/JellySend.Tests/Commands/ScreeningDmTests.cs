using JellySend.Commands.Events;
using JellySend.Commands.Media;
using JellySend.Streaming.Engine;

namespace JellySend.Tests.Commands;

/// <summary>Spec 031: titles with no single match are settled with the event's creator in a DM.</summary>
public sealed class ScreeningDmTests
{
    private const ulong Channel = 300;
    private const ulong Dm = 900;
    private const ulong Creator = 20;
    private readonly MediaHarness _h = new();
    private ulong? _dm = Dm;

    public ScreeningDmTests()
    {
        _h.Library.Search["first film"] = [MediaHarness.Movie("f1", "First Film", 2001, 90L * 60 * 10_000_000)];
        _h.Library.Search["second film"] = [MediaHarness.Movie("f2", "Second Film", 2002, 60L * 60 * 10_000_000)];
        _h.Library.Search["twin"] = [MediaHarness.Movie("t1", "Twin", 1990), MediaHarness.Movie("t2", "Twin", 2020)];
    }

    private MediaModule.ScreeningRequest Request(params string[] lines) =>
        new(5, Channel, "Movie Night", lines, Creator, m => m.AuthorId == Creator, () => Task.CompletedTask)
        {
            EventId = 77,
            OpenDm = () => Task.FromResult(_dm),
        };

    private IEnumerable<string> In(ulong channel)
    {
        lock (_h.Sender.Sent)
        {
            return [.. _h.Sender.Sent.Where(s => s.Channel == channel).Select(s => s.Content)];
        }
    }

    private Task Reply(string text, ulong author = Creator) => _h.Send(text, guild: null, author: author, channel: Dm);

    private async Task WhenAsked(string start) =>
        await _h.Until(() => In(Dm).Any(t => t.Contains(start, StringComparison.Ordinal)) && _h.Hub.WaiterCount > 0, start);

    [Fact]
    public async Task A_missing_title_is_replaced_by_the_creator_in_a_dm()
    {
        var run = _h.Media.RunScreeningAsync(Request("first film", "lost film"), TestContext.Current.CancellationToken);
        await WhenAsked("the screening `lost film` isn't in the library");
        Assert.StartsWith("🎬 **Movie Night** in **Movie Night**: the screening", In(Dm).First(), StringComparison.Ordinal);
        Assert.Contains("🎬 Screening 2, `lost film`, needs a word with the event's creator — asking them now.", In(Channel));

        await Reply("nope", author: 30);                                 // someone else in that DM: ignored
        await Reply("still lost");                                       // nothing found: asked again
        await _h.Until(() => In(Dm).Contains("Try another title, or `skip`."), "asked again");
        await Reply("second film");
        await run;
        Assert.Contains("✅ Got it: **Second Film (2002)** instead of `lost film`.", In(Dm));
        var schedule = In(Channel).Last(t => t.Contains("screening times", StringComparison.Ordinal));
        Assert.Contains("Second Film (2002)", schedule, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skip_leaves_it_out()
    {
        var run = _h.Media.RunScreeningAsync(Request("first film", "lost film"), TestContext.Current.CancellationToken);
        await WhenAsked("isn't in the library");
        await Reply("SKIP");
        await run;
        Assert.Contains("👍 `lost film` is left out.", In(Dm));
        Assert.Contains("🎬 Screening 2, `lost film`, is left out (the event's creator skipped it).", In(Channel));
    }

    [Fact]
    public async Task No_answer_before_it_is_due_skips_it_and_says_so()
    {
        var run = _h.Media.RunScreeningAsync(Request("first film", "lost film"), TestContext.Current.CancellationToken);
        await WhenAsked("isn't in the library");
        _h.Time.Advance(TimeSpan.FromMinutes(89));                       // due at 90 min, closes 1 min before
        await run;
        Assert.Contains("🎬 Screening 2, `lost film`, is skipped — no answer from the event's creator.", In(Channel));
    }

    [Fact]
    public async Task Several_matches_are_picked_by_the_creator_in_the_dm()
    {
        var run = _h.Media.RunScreeningAsync(Request("first film", "twin"), TestContext.Current.CancellationToken);
        await WhenAsked("🔎 Found 2 matches for `twin`");
        Assert.Contains(In(Dm), t => t.Contains("the screening `twin` matches several things", StringComparison.Ordinal));
        Assert.DoesNotContain(In(Channel), t => t.StartsWith("🔎 Found", StringComparison.Ordinal)); // not in the channel
        await Reply("2");
        await run;
        Assert.Contains("✅ Got it: **Twin (2020)**.", In(Dm));
    }

    [Fact]
    public async Task Without_a_dm_it_works_as_before()
    {
        _dm = null;
        var run = _h.Media.RunScreeningAsync(Request("first film", "lost film", "twin"), TestContext.Current.CancellationToken);
        await _h.Until(() => In(Channel).Any(t => t.StartsWith("🔎 Found 2 matches", StringComparison.Ordinal)) && _h.Hub.WaiterCount > 0, "the channel picker");
        Assert.Contains("🔎 `lost film` isn't in the library — skipped.", In(Channel));
        await _h.Send("1", author: Creator, channel: Channel);
        await run;
        Assert.Empty(In(Dm));
    }

    [Fact]
    public async Task Answers_given_ahead_of_time_are_used_at_the_start()
    {
        var check = _h.Media.CheckScreeningsAsync(Request("lost film", "twin", "gone too"), _h.Time.GetUtcNow().AddHours(2), TestContext.Current.CancellationToken);
        await WhenAsked("the screening `lost film` isn't in the library");
        await Reply("second film");
        await WhenAsked("🔎 Found 2 matches for `twin`");
        await Reply("1");
        await WhenAsked("the screening `gone too` isn't in the library");
        await Reply("skip");
        await check;

        _h.Sender.Sent.Clear();
        await _h.Media.RunScreeningAsync(Request("lost film", "twin", "gone too"), TestContext.Current.CancellationToken);
        Assert.Empty(In(Dm));                                            // nothing to ask again
        Assert.Equal("Second Film (2002)", _h.Current.Request.Title);
        Assert.Contains(In(Channel), t => t.Contains("Twin (1990)", StringComparison.Ordinal) && t.Contains("screening times", StringComparison.Ordinal));
        Assert.Contains("🎬 `gone too` is left out (the event's creator skipped it).", In(Channel));
    }

    [Fact]
    public async Task An_unanswered_check_asks_again_at_the_start()
    {
        var check = _h.Media.CheckScreeningsAsync(Request("first film", "lost film"), _h.Time.GetUtcNow().AddMinutes(30), TestContext.Current.CancellationToken);
        await WhenAsked("isn't in the library");
        _h.Time.Advance(TimeSpan.FromMinutes(30));
        await check;

        _h.Sender.Sent.Clear();
        var run = _h.Media.RunScreeningAsync(Request("first film", "lost film"), TestContext.Current.CancellationToken);
        await WhenAsked("isn't in the library");
        await Reply("second film");
        await run;
        Assert.Contains("Second Film (2002)", In(Channel).Last(t => t.Contains("screening times", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_with_nothing_to_ask_or_no_dm_says_nothing()
    {
        await _h.Media.CheckScreeningsAsync(Request("first film", "second film"), _h.Time.GetUtcNow().AddHours(1), TestContext.Current.CancellationToken);
        _dm = null;
        await _h.Media.CheckScreeningsAsync(Request("lost film"), _h.Time.GetUtcNow().AddHours(1), TestContext.Current.CancellationToken);
        Assert.Empty(_h.Texts);
    }

    [Fact]
    public async Task A_cancelled_check_stops_quietly()
    {
        using var cts = new CancellationTokenSource();
        var check = _h.Media.CheckScreeningsAsync(Request("lost film"), _h.Time.GetUtcNow().AddHours(1), cts.Token);
        await WhenAsked("isn't in the library");
        await cts.CancelAsync();
        await check;
        Assert.Equal(0, _h.Hub.WaiterCount);
    }
}

/// <summary>Spec 031: the scheduler checks upcoming events, and again when they're edited.</summary>
public sealed partial class EventSchedulerTests
{
    [Fact]
    public async Task An_upcoming_event_is_checked_once_and_again_when_edited()
    {
        _h.Library.Search["twin"] = [MediaHarness.Movie("t1", "Twin", 1990), MediaHarness.Movie("t2", "Twin", 2020)];
        _events.Add(Event(startInMinutes: 60, description: "Screenings:\n1. lost film"));
        await _scheduler.PollAsync(false, TestContext.Current.CancellationToken);
        await _h.Until(() => _h.Texts.Any(t => t.Contains("`lost film` isn't in the library", StringComparison.Ordinal)), "the DM");
        await _scheduler.PollAsync(false, TestContext.Current.CancellationToken);  // unchanged: not asked again
        Assert.Single(_h.Texts, t => t.Contains("`lost film` isn't in the library", StringComparison.Ordinal));

        _events.Replace(Event(startInMinutes: 60, description: "Screenings:\n1. twin"));
        await _scheduler.PollAsync(false, TestContext.Current.CancellationToken);
        await _h.Until(() => _h.Texts.Any(t => t.Contains("`twin` matches several things", StringComparison.Ordinal)), "the new DM");
        Assert.Empty(_events.StatusChanges);
    }

    [Fact]
    public async Task The_bot_marks_itself_interested_in_events_it_will_run_once()
    {
        _events.Add(Event(id: 1, startInMinutes: 60));
        await PollAsync();
        await PollAsync();
        Assert.Equal([(1UL, true)], _events.Interest);

        _events.Replace(Event(id: 1, startInMinutes: 60, description: "Movie night, no list"));  // edited: not the bot's any more
        await PollAsync();
        Assert.Equal([(1UL, true), (1UL, false)], _events.Interest);
    }

    [Fact]
    public async Task No_interest_in_events_it_will_not_run()
    {
        _events.Add(Event(id: 1, startInMinutes: 60, creator: 30));               // creator can't use the bot
        _events.Add(Event(id: 2, startInMinutes: 60, description: "No list here"));
        _events.Add(Event(id: 3, status: EventInfo.Active));                       // already running at startup
        await PollAsync(startup: true);
        Assert.Empty(_events.Interest);
    }

    [Fact]
    public async Task A_due_event_is_marked_as_it_starts_and_a_refusal_is_not_retried()
    {
        _events.InterestError = new InvalidOperationException("403");
        _events.Add(Event(startInMinutes: 60));
        await PollAsync();
        await PollAsync();
        Assert.Empty(_events.Interest);
        Assert.Empty(_events.StatusChanges);
        _events.InterestError = null;
        _h.Time.Advance(TimeSpan.FromMinutes(60));
        await PollAsync();
        Assert.Empty(_events.Interest);                                            // remembered: not retried
        Assert.Single(_events.StatusChanges);                                      // and it still starts
    }

    [Fact]
    public async Task Upcoming_lists_only_events_the_bot_will_run()
    {
        _events.Add(Event(id: 1, startInMinutes: 60));
        _events.Add(Event(id: 2, startInMinutes: 60, creator: 30));                // can't use the bot
        _events.Add(Event(id: 3, startInMinutes: 60, description: "No list"));
        _events.Add(Event(id: 4, status: EventInfo.Active));                       // already on
        _events.Add(Event(id: 5, startInMinutes: 60, entity: 3));                  // not a voice event
        await _scheduler.PollAsync(true, TestContext.Current.CancellationToken);
        var upcoming = Assert.Single(_scheduler.Upcoming());
        Assert.Equal((1UL, 5UL, "Movie Night", 20UL, 300UL), (upcoming.EventId, upcoming.Guild, upcoming.Name, upcoming.CreatorId, upcoming.Channel));
        Assert.Equal(["First Film"], upcoming.Lines);
    }

    [Fact]
    public async Task Upcoming_events_from_others_are_not_checked()
    {
        _events.Add(Event(startInMinutes: 60, creator: 30, description: "Screenings:\n1. lost film"));
        await _scheduler.PollAsync(false, TestContext.Current.CancellationToken);
        await Task.WhenAll(_scheduler.Checks);
        Assert.Empty(_scheduler.Checks);
        Assert.Empty(_h.Texts);
    }
}

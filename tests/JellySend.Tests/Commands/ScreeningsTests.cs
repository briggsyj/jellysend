using JellySend.Commands.Events;
using JellySend.Commands.Media;
using JellySend.Commands.Routing;
using JellySend.Services.Jellyfin;
using JellySend.Streaming.Engine;

namespace JellySend.Tests.Commands;

/// <summary>Spec 028: the Screenings list in an event description.</summary>
public sealed class ScreeningsParseTests
{
    [Theory]
    [InlineData("Screenings:\n1. Big Buck Bunny\n2. Example Show s1e1-3")]
    [InlineData("Movie night!\n\nscreenings\n- Big Buck Bunny\n- Example Show s1e1-3\n\nBring snacks")]
    [InlineData("**SCREENINGS:**\r\nBig Buck Bunny\r\nExample Show s1e1-3")]
    [InlineData("🎬 Screenings :\n\n* Big Buck Bunny\n• Example Show s1e1-3\n")]
    [InlineData("Screenings\n1) Big Buck Bunny\n2) Example Show s1e1-3")]
    public void Lists_in_every_style(string description) =>
        Assert.Equal(["Big Buck Bunny", "Example Show s1e1-3"], Screenings.Parse(description));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Watch party tonight")]
    [InlineData("Our screenings: big buck bunny")]  // not on a line of its own
    public void Not_the_bots_event(string? description) => Assert.Null(Screenings.Parse(description));

    [Fact]
    public void A_marker_with_nothing_after_is_an_empty_list() => Assert.Empty(Screenings.Parse("Screenings:\n\n")!);

    [Fact]
    public void Numbers_in_titles_stay() => Assert.Equal(["2001 a space film", "Show 2x5"], Screenings.Parse("Screenings\n1. 2001 a space film\nShow 2x5"));
}

/// <summary>Spec 028: an event's screenings take over the stream, get picked where needed, and end the event.</summary>
public sealed class ScreeningRunTests
{
    private const ulong Channel = 300;
    private readonly MediaHarness _h = new();
    private readonly List<string> _ended = [];

    public ScreeningRunTests()
    {
        _h.Library.Search["first film"] = [MediaHarness.Movie("f1", "First Film", 2001, 90L * 60 * 10_000_000)];
        _h.Library.Search["second film"] = [MediaHarness.Movie("f2", "Second Film", 2002, 60L * 60 * 10_000_000)];
        _h.Library.Search["twin"] = [MediaHarness.Movie("t1", "Twin", 1990, 30L * 60 * 10_000_000), MediaHarness.Movie("t2", "Twin", 2020, 45L * 60 * 10_000_000)];
        _h.Library.Search["other"] = [MediaHarness.Movie("o", "Other", 2000)];
    }

    private MediaModule.ScreeningRequest Request(params string[] lines) => new(
        5, Channel, "Movie Night", lines, 20,
        m => m.AuthorId == 20,
        () =>
        {
            _ended.Add("ended");
            return Task.CompletedTask;
        });

    private IEnumerable<string> InChannel()
    {
        lock (_h.Sender.Sent)
        {
            return [.. _h.Sender.Sent.Where(s => s.Channel == Channel).Select(s => s.Content)];
        }
    }

    private Task Answer(string text, ulong author = 20) => _h.Send(text, author: author, channel: Channel);

    [Fact]
    public async Task Single_matches_stream_in_the_event_channel_with_a_schedule_and_end_the_event()
    {
        await _h.Media.RunScreeningAsync(Request("first film", "second film"), TestContext.Current.CancellationToken);
        Assert.Equal(Channel, _h.Gateway.Links[^1].ChannelId);
        Assert.Equal("First Film (2001)", _h.Current.Request.Title);
        var schedule = InChannel().Last(t => t.Contains("screening times", StringComparison.Ordinal));
        var start = _h.Time.GetUtcNow().ToUnixTimeSeconds();
        Assert.Contains($"`1` <t:{start}:t> First Film (2001)", schedule, StringComparison.Ordinal);
        Assert.Contains($"`2` <t:{start + 90 * 60}:t> Second Film (2002)", schedule, StringComparison.Ordinal);
        Assert.Contains($"🏁 Ends around <t:{start + 150 * 60}:t>", schedule, StringComparison.Ordinal);

        _h.Current.End(PipelineEndReason.Natural);
        await _h.Until(() => _h.Current.Request.Title == "Second Film (2002)" && _h.Current.Started);
        Assert.Empty(_ended);
        _h.Current.End(PipelineEndReason.Natural);
        await _h.Until(() => _ended.Count == 1, "the event ended");
        Assert.Contains("🎬 That's all the screenings — thanks for watching! 📴 Signing off.", InChannel());
        await _h.Until(() => _h.Voice.Get(5) is null, "signed off");   // spec 030: untouched, the bot leaves
        Assert.False(_h.Media.IsStreaming(5));
    }

    [Fact]
    public async Task The_event_takes_over_and_untouched_signs_off_at_the_end()
    {
        await _h.Send("!play other");
        _h.Library.Search["queued"] = [MediaHarness.Movie("q", "Queued", 2000)];
        await _h.Send("!playlist add queued");
        await _h.Media.RunScreeningAsync(Request("first film"), TestContext.Current.CancellationToken);
        Assert.Equal("First Film (2001)", _h.Current.Request.Title);
        var created = _h.Pipelines.Created.Count;
        _h.Current.End(PipelineEndReason.Natural);
        await _h.Until(() => _h.Voice.Get(5) is null, "signed off");
        Assert.Single(_ended);
        Assert.Equal(created, _h.Pipelines.Created.Count);           // what was queued before the event doesn't play
    }

    [Fact]
    public async Task Several_matches_are_asked_in_the_channel_and_the_schedule_is_reposted()
    {
        var run = _h.Media.RunScreeningAsync(Request("first film", "twin", "second film"), TestContext.Current.CancellationToken);
        await _h.Until(() => InChannel().Any(t => t.StartsWith("🔎 Found 2 matches for `twin`", StringComparison.Ordinal)), "the picker");
        var before = InChannel().Last(t => t.Contains("screening times", StringComparison.Ordinal));
        Assert.Contains("⏳ `twin` — waiting for a pick", before, StringComparison.Ordinal);
        Assert.Contains("🏁 End time to follow", before, StringComparison.Ordinal);

        await Answer("2", author: 30);                                // a viewer: not allowed to pick
        Assert.False(run.IsCompleted);
        await _h.Until(() => _h.Hub.WaiterCount > 0);
        await Answer("2");
        await run;
        var after = InChannel().Last(t => t.Contains("screening times", StringComparison.Ordinal));
        var start = _h.Time.GetUtcNow().ToUnixTimeSeconds();
        Assert.Contains($"`2` <t:{start + 90 * 60}:t> Twin (2020)", after, StringComparison.Ordinal);
        Assert.Contains($"`3` <t:{start + 135 * 60}:t> Second Film (2002)", after, StringComparison.Ordinal);
        Assert.Contains($"🏁 Ends around <t:{start + 195 * 60}:t>", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unanswered_pick_takes_the_first_match_shortly_before_it_is_due()
    {
        var run = _h.Media.RunScreeningAsync(Request("first film", "twin"), TestContext.Current.CancellationToken);
        await _h.Until(() => _h.Hub.WaiterCount > 0, "the picker");
        _h.Time.Advance(TimeSpan.FromMinutes(88));                    // due at 90 min, closes 1 min before
        Assert.False(run.IsCompleted);
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await run;
        Assert.Contains("⌛ No answer — going with the first one.", InChannel());
        Assert.Contains(InChannel(), t => t.Contains("Twin (1990)", StringComparison.Ordinal) && t.Contains("screening times", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_first_item_that_needs_a_pick_waits_for_it_then_starts()
    {
        var run = _h.Media.RunScreeningAsync(Request("twin", "first film"), TestContext.Current.CancellationToken);
        await _h.Until(() => _h.Hub.WaiterCount > 0, "the picker");
        Assert.Empty(_h.Pipelines.Created);                          // nothing to play until the first is picked
        await Answer("1");
        await run;
        Assert.Equal("Twin (1990)", _h.Current.Request.Title);
        Assert.Equal(Channel, _h.Gateway.Links[^1].ChannelId);
    }

    [Fact]
    public async Task Missing_titles_are_skipped_and_said()
    {
        await _h.Media.RunScreeningAsync(Request("nothing like it", "first film"), TestContext.Current.CancellationToken);
        Assert.Contains("🔎 `nothing like it` isn't in the library — skipped.", InChannel());
        Assert.Equal("First Film (2001)", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Without_the_library_nothing_happens()
    {
        var h = new MediaHarness(configured: false);
        await h.Media.RunScreeningAsync(Request("first film"), TestContext.Current.CancellationToken);
        Assert.Empty(h.Texts);
    }
}

/// <summary>Spec 030: an untouched event holds its channel; a changed playlist hands control (and the timeout) back.</summary>
public sealed class ScreeningControlTests
{
    private const ulong Channel = 300;
    private readonly MediaHarness _h = new(emptyChannelTimeout: TimeSpan.FromMinutes(20));
    private readonly List<string> _ended = [];

    public ScreeningControlTests()
    {
        _h.Library.Search["first film"] = [MediaHarness.Movie("f1", "First Film", 2001, 90L * 60 * 10_000_000)];
        _h.Library.Search["second film"] = [MediaHarness.Movie("f2", "Second Film", 2002, 60L * 60 * 10_000_000)];
        _h.Library.Search["other"] = [MediaHarness.Movie("o", "Other", 2000)];
    }

    private Task Run() => _h.Media.RunScreeningAsync(new MediaModule.ScreeningRequest(
        5, Channel, "Movie Night", ["first film", "second film"], 20, m => m.AuthorId == 20,
        () =>
        {
            _ended.Add("ended");
            return Task.CompletedTask;
        }), TestContext.Current.CancellationToken);

    [Fact]
    public async Task An_untouched_event_ignores_the_empty_channel_timeout()
    {
        await Run();
        Assert.True(_h.Media.HoldsChannel(5));
        await _h.VoiceCommands.WatchIfEmptyAsync(5);                 // nobody but the bot in the event's channel
        _h.Gateway.States[(5, 30)] = Channel;
        _h.Gateway.States.Remove((5, 30));
        await _h.VoiceCommands.OnVoiceStateChangedAsync(5, 30, Channel, null); // the last person leaves
        Assert.False(_h.VoiceCommands.EmptyCountdowns.ContainsKey(5));
        _h.Time.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(_h.Media.IsStreaming(5));
    }

    [Theory]
    [InlineData("!playlist add other")]
    [InlineData("!play other")]
    [InlineData("!skip")]
    [InlineData("!playlist remove 1")]
    public async Task Changing_the_playlist_hands_control_back_and_starts_the_timeout(string command)
    {
        await Run();
        _h.Gateway.States.Remove((5, 20));                            // typed from text, not in voice: the bot stays put
        _h.Sender.Sent.Clear();
        await _h.Send(command);
        Assert.False(_h.Media.HoldsChannel(5));
        Assert.Contains(_h.Texts, t => t.StartsWith("🎛️ The playlist's been changed", StringComparison.Ordinal));
        Assert.True(_h.VoiceCommands.EmptyCountdowns.ContainsKey(5));  // nobody in the channel: the countdown runs
        _h.Time.Advance(TimeSpan.FromMinutes(20));
        await _h.VoiceCommands.EmptyCountdowns[5];
        Assert.Null(_h.Voice.Get(5));                                 // signed off by the timeout
    }

    [Fact]
    public async Task A_failed_change_is_not_a_change()
    {
        await Run();
        await _h.Send("!playlist remove 9");
        await _h.Send("!playlist order 1 9");
        Assert.True(_h.Media.HoldsChannel(5));
    }

    [Fact]
    public async Task Under_manual_control_the_bot_carries_on_after_the_last_screening()
    {
        await Run();
        _h.Gateway.States[(5, 20)] = Channel;                         // someone is watching: no timeout
        await _h.Send("!playlist add other");                         // after the screenings
        _h.Current.End(PipelineEndReason.Natural);
        await _h.Until(() => _h.Current.Request.Title == "Second Film (2002)" && _h.Current.Started);
        _h.Current.End(PipelineEndReason.Natural);
        await _h.Until(() => _ended.Count == 1, "the event ended");
        await _h.Until(() => _h.Current.Request.Title == "Other (2000)" && _h.Current.Started, "the added item");
        Assert.NotNull(_h.Voice.Get(5));
        Assert.Contains("🎬 That's all the screenings — thanks for watching!", _h.Texts);
    }
}

/// <summary>Spec 028: which events start the bot, and when.</summary>
public sealed partial class EventSchedulerTests
{
    private readonly MediaHarness _h = new();
    private readonly FakeEvents _events = new();
    private readonly EventScheduler _scheduler;

    public EventSchedulerTests()
    {
        _h.Library.Search["first film"] = [MediaHarness.Movie("f1", "First Film", 2001)];
        _scheduler = new EventScheduler(_events, _h.Media, new FakeRoles(), _h.Sender, _h.Time);
    }

    private EventInfo Event(ulong id = 1, int status = EventInfo.Scheduled, double startInMinutes = -1, ulong creator = 20,
        string description = "Screenings:\n1. First Film", int entity = EventInfo.VoiceEntity, ulong? channel = 300) =>
        new(id, 5, "Movie Night", description, channel, status, entity, _h.Time.GetUtcNow().AddMinutes(startInMinutes), creator);

    private async Task PollAsync(bool startup = false)
    {
        await _scheduler.PollAsync(startup, TestContext.Current.CancellationToken);
        await Task.WhenAll(_scheduler.Runs);
    }

    [Fact]
    public async Task A_due_event_is_started_and_its_screenings_run_once()
    {
        _events.Add(Event());
        await PollAsync();
        Assert.Equal([(1UL, EventInfo.Active)], _events.StatusChanges);
        Assert.Equal("First Film (2001)", _h.Current.Request.Title);
        await PollAsync();                                           // already handled
        Assert.Single(_events.StatusChanges);
        Assert.Single(_h.Pipelines.Created);
    }

    [Fact]
    public async Task Not_yet_due_events_wait()
    {
        _events.Add(Event(startInMinutes: 10));
        await PollAsync();
        Assert.Empty(_events.StatusChanges);
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        await PollAsync();
        Assert.Single(_events.StatusChanges);
    }

    [Fact]
    public async Task An_event_started_by_hand_runs_but_not_one_already_running_at_startup()
    {
        _events.Add(Event(id: 1, status: EventInfo.Active));
        await PollAsync(startup: true);
        Assert.Empty(_h.Pipelines.Created);
        _events.Add(Event(id: 2, status: EventInfo.Active));
        await PollAsync();
        Assert.Single(_h.Pipelines.Created);
        Assert.Empty(_events.StatusChanges);                         // already active: not started again
    }

    [Theory]
    [InlineData(30UL)]   // a viewer
    [InlineData(99UL)]   // nobody the bot knows
    public async Task Events_from_people_who_cannot_use_the_bot_are_refused_once(ulong creator)
    {
        _events.Add(Event(creator: creator));
        await PollAsync();
        await PollAsync();
        Assert.Empty(_events.StatusChanges);
        Assert.Empty(_h.Pipelines.Created);
        Assert.Single(_h.Texts, t => t.StartsWith("🙅 **Movie Night** lists screenings", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Movie night with friends", EventInfo.VoiceEntity, 300UL)]   // no Screenings list
    [InlineData("Screenings:\n1. First Film", 3, 300UL)]                     // an external event
    [InlineData("Screenings:\n1. First Film", EventInfo.VoiceEntity, null)]
    [InlineData("Screenings:\n", EventInfo.VoiceEntity, 300UL)]              // an empty list
    public async Task Other_events_are_ignored(string description, int entity, ulong? channel)
    {
        _events.Add(Event(description: description, entity: entity, channel: channel));
        await PollAsync();
        Assert.Empty(_events.StatusChanges);
        Assert.Empty(_h.Texts);
    }

    [Fact]
    public async Task A_start_the_bot_may_not_make_still_runs_the_screenings()
    {
        _events.Add(Event());
        _events.StatusError = new InvalidOperationException("403");
        await PollAsync();
        Assert.Contains(_h.Texts, t => t.Contains("needs **Manage Events**", StringComparison.Ordinal));
        Assert.Equal("First Film (2001)", _h.Current.Request.Title);
    }

    [Fact]
    public async Task Listing_failures_are_survived()
    {
        _events.ServersError = new InvalidOperationException("down");
        await PollAsync();
        _events.ServersError = null;
        _events.EventsError = new InvalidOperationException("down");
        _events.Add(Event());
        await PollAsync();
        Assert.Empty(_h.Pipelines.Created);
    }

    [Fact]
    public async Task The_loop_polls_until_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var loop = _scheduler.RunAsync(cts.Token);
        await _h.Until(() => _events.Polls >= 1);
        _h.Time.Advance(EventScheduler.PollInterval);
        await _h.Until(() => _events.Polls >= 2);
        await cts.CancelAsync();
        await loop;
    }

    private sealed class FakeEvents : IEventSource
    {
        private readonly List<EventInfo> _events = [];

        public List<(ulong Event, int Status)> StatusChanges { get; } = [];

        public Exception? StatusError { get; set; }

        public Exception? ServersError { get; set; }

        public Exception? EventsError { get; set; }

        public int Polls { get; private set; }

        public void Add(EventInfo e) => _events.Add(e);

        public void Replace(EventInfo e)
        {
            _events.RemoveAll(x => x.Id == e.Id);
            _events.Add(e);
        }

        /// <summary>The DM channel with any user (spec 031); null = can't be messaged.</summary>
        public ulong? Dm { get; set; } = 900;

        public Task<ulong?> OpenDmAsync(ulong userId, CancellationToken ct) => Task.FromResult(Dm);

        /// <summary>Interest changes (spec 033): event, interested.</summary>
        public List<(ulong Event, bool Interested)> Interest { get; } = [];

        public Exception? InterestError { get; set; }

        public Task SetInterestedAsync(ulong guildId, ulong eventId, bool interested, CancellationToken ct)
        {
            if (InterestError is { } e)
            {
                return Task.FromException(e);
            }

            Interest.Add((eventId, interested));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ulong>> GetServerIdsAsync(CancellationToken ct)
        {
            Polls++;
            return ServersError is { } e ? Task.FromException<IReadOnlyList<ulong>>(e) : Task.FromResult<IReadOnlyList<ulong>>([5]);
        }

        public Task<IReadOnlyList<EventInfo>> GetEventsAsync(ulong guildId, CancellationToken ct) =>
            EventsError is { } e ? Task.FromException<IReadOnlyList<EventInfo>>(e) : Task.FromResult<IReadOnlyList<EventInfo>>([.. _events]);

        public Task SetStatusAsync(ulong guildId, ulong eventId, int status, CancellationToken ct)
        {
            if (StatusError is { } e)
            {
                return Task.FromException(e);
            }

            StatusChanges.Add((eventId, status));
            return Task.CompletedTask;
        }
    }
}

using System.Globalization;
using System.Text.Json;
using JellySend.Commands.Media;
using JellySend.Commands.Routing;
using JellySend.Common.Config;
using JellySend.Common.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellySend.Commands.Events;

/// <summary>A Discord scheduled event (spec 028). Status: 1 scheduled, 2 active, 3 completed, 4 cancelled.</summary>
public sealed record EventInfo(
    ulong Id, ulong GuildId, string Name, string Description, ulong? ChannelId, int Status, int EntityType, DateTimeOffset Start, ulong? CreatorId)
{
    public const int Scheduled = 1;
    public const int Active = 2;
    public const int Completed = 3;
    public const int VoiceEntity = 2;
}

/// <summary>Discord's scheduled events (REST in production, a fake in tests).</summary>
public interface IEventSource
{
    Task<IReadOnlyList<ulong>> GetServerIdsAsync(CancellationToken ct);

    Task<IReadOnlyList<EventInfo>> GetEventsAsync(ulong guildId, CancellationToken ct);

    /// <summary>Set the event's status: <see cref="EventInfo.Active"/> starts it, <see cref="EventInfo.Completed"/> ends it.</summary>
    Task SetStatusAsync(ulong guildId, ulong eventId, int status, CancellationToken ct);

    /// <summary>The direct-message channel with <paramref name="userId"/> (spec 031), or null when it can't be opened.</summary>
    Task<ulong?> OpenDmAsync(ulong userId, CancellationToken ct);

    /// <summary>Mark the bot's account as interested in the event, or not (spec 033).</summary>
    Task SetInterestedAsync(ulong guildId, ulong eventId, bool interested, CancellationToken ct);
}

/// <summary>
/// Spec 028: watches every server's scheduled events. A voice-channel event whose description has a <c>Screenings</c>
/// list, created by someone allowed to <c>!play</c>, is started by the bot at its scheduled time (or picked up when
/// someone starts it earlier), and its screenings run in the event's channel.
/// </summary>
public sealed class EventScheduler(
    IEventSource source, MediaModule media, IRoleDirectory roles, IMessageSender announcer,
    TimeProvider? time = null, ILogger<EventScheduler>? logger = null, SettingsStore? settings = null) : MediaModule.IEventControl
{
    /// <summary>Spec 038: where opt-outs (<c>!event unsubscribe</c>) are kept.</summary>
    private readonly SettingsStore _settings = settings ?? new SettingsStore(null);

    /// <summary>
    /// How often the known events are looked at (no network): the most an event can start late. Spec 034: changes
    /// arrive pushed over the gateway, so Discord is only asked for the full list every <see cref="FullRefreshInterval"/>.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Spec 034: how often every server's events are fetched over REST — a safety net for missed pushes.</summary>
    public static readonly TimeSpan FullRefreshInterval = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _log = logger ?? NullLogger<EventScheduler>.Instance;
    private readonly HashSet<ulong> _handled = [];

    /// <summary>Spec 033: events the bot marked itself interested in (it will run them).</summary>
    private readonly HashSet<ulong> _interested = [];

    /// <summary>Spec 031: per upcoming event, the description last checked and that check's cancellation.</summary>
    private readonly Dictionary<ulong, (string Description, DateTimeOffset Start, CancellationTokenSource Cancel)> _checked = [];

    /// <summary>The screenings started so far (tests await them).</summary>
    internal List<Task> Runs { get; } = [];

    /// <summary>The ahead-of-time checks started so far (spec 031; tests await them).</summary>
    internal List<Task> Checks { get; } = [];

    /// <summary>Spec 034: every event the bot knows of, kept current by gateway pushes and the full refresh.</summary>
    private readonly Dictionary<ulong, EventInfo> _known = [];

    /// <summary>Set when a push arrives, to look at it straight away.</summary>
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>
    /// Until <paramref name="ct"/> is cancelled: fetch every server's events now and every
    /// <see cref="FullRefreshInterval"/>, and look at the known events every <see cref="PollInterval"/> or as soon
    /// as a change is pushed.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await PollAsync(startup: true, ct).ConfigureAwait(false);
        var lastRefresh = _time.GetUtcNow();
        while (!ct.IsCancellationRequested)
        {
            // Whichever comes first: a pushed change, or the next regular look (on the bot's clock).
            using (var lap = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var woke = _wake.WaitAsync(lap.Token);
                var timer = Task.Delay(PollInterval, _time, lap.Token);
                await Task.WhenAny(woke, timer).ConfigureAwait(false);
                await lap.CancelAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (_time.GetUtcNow() - lastRefresh >= FullRefreshInterval)
            {
                await PollAsync(startup: false, ct).ConfigureAwait(false);
                lastRefresh = _time.GetUtcNow();
            }
            else
            {
                await TickAsync(startup: false, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Spec 034: a gateway dispatch. Scheduled-event changes update the known events (and wake the loop); a
    /// guild's arrival brings its events along.
    /// </summary>
    public void Apply(string type, JsonElement data)
    {
        var changed = false;
        lock (_known)
        {
            switch (type)
            {
                case "GUILD_SCHEDULED_EVENT_CREATE" or "GUILD_SCHEDULED_EVENT_UPDATE" when Parse(data) is { } e:
                    _known[e.Id] = e;
                    changed = true;
                    break;
                case "GUILD_SCHEDULED_EVENT_DELETE" when Snowflake(data, "id") is { } id:
                    changed = _known.Remove(id);
                    break;
                case "GUILD_CREATE" when data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("guild_scheduled_events", out var events) && events.ValueKind == JsonValueKind.Array:
                    foreach (var e in events.EnumerateArray().Select(Parse).OfType<EventInfo>())
                    {
                        _known[e.Id] = e;
                        changed = true;
                    }

                    break;
            }
        }

        if (changed)
        {
            _wake.Release();
        }
    }

    /// <summary>
    /// The full refresh: every server's events over REST, replacing what's known, then a look at them. On the first
    /// (<paramref name="startup"/>), events already running are left alone: they were started before this process.
    /// </summary>
    public async Task PollAsync(bool startup, CancellationToken ct)
    {
        IReadOnlyList<ulong> servers;
        try
        {
            servers = await source.GetServerIdsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "could not list servers for scheduled events");
            await TickAsync(startup, ct).ConfigureAwait(false);
            return;
        }

        foreach (var guild in servers)
        {
            IReadOnlyList<EventInfo> events;
            try
            {
                events = await source.GetEventsAsync(guild, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "could not list the scheduled events of guild {Guild}", guild);
                continue;
            }

            lock (_known)
            {
                foreach (var stale in _known.Values.Where(k => k.GuildId == guild).Select(k => k.Id).ToList())
                {
                    _known.Remove(stale);
                }

                foreach (var e in events)
                {
                    _known[e.Id] = e;
                }
            }
        }

        await TickAsync(startup, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Spec 038: upcoming events with a list from someone allowed to <c>!play</c>, in every server, soonest first —
    /// the subscribed ones (the bot will run them), or the ones opted out with <c>!event unsubscribe</c>.
    /// </summary>
    public IReadOnlyList<MediaModule.EventChoice> Choices(bool subscribed)
    {
        lock (_known)
        {
            return [.. Listed()
                .Where(e => _settings.IsEventUnsubscribed(e.Id) != subscribed)
                .OrderBy(e => e.Start)
                .Select(e => new MediaModule.EventChoice(e.Id, e.GuildId, e.Name, e.Start))];
        }
    }

    /// <summary>
    /// Spec 038: subscribe to an event again, or opt it out (saved). The bot's Interested mark and any open questions
    /// to the creator follow on the next look, which this triggers at once.
    /// </summary>
    public bool SetSubscribed(ulong eventId, bool subscribed)
    {
        var saved = _settings.SetEventUnsubscribed(eventId, !subscribed);
        _wake.Release();
        return saved;
    }

    /// <summary>Scheduled voice events with a list from someone allowed to <c>!play</c>. Caller holds <c>_known</c>.</summary>
    private IEnumerable<EventInfo> Listed() =>
        _known.Values.Where(e => e.Status == EventInfo.Scheduled && e.EntityType == EventInfo.VoiceEntity && e.ChannelId is not null
            && e.CreatorId is { } creator && RoleResolver.Satisfies(roles, creator, Role.Friend)
            && Screenings.Parse(e.Description) is { Count: > 0 });

    /// <summary>
    /// Spec 036: the upcoming events the bot will run — scheduled voice events with a Screenings list, from someone
    /// allowed to <c>!play</c> — for <c>!schedule events</c>.
    /// </summary>
    public IReadOnlyList<MediaModule.UpcomingScreening> Upcoming()
    {
        lock (_known)
        {
            return [.. Listed()
                .Where(e => !_settings.IsEventUnsubscribed(e.Id))
                .Select(e => (Event: e, Lines: Screenings.Parse(e.Description)))
                .Where(x => x.Lines is { Count: > 0 })
                .Select(x => new MediaModule.UpcomingScreening(
                    x.Event.Id, x.Event.GuildId, x.Event.Name, x.Event.Start, x.Lines!, x.Event.CreatorId!.Value, x.Event.ChannelId!.Value))];
        }
    }

    /// <summary>Look at every known event (no network): start what's due, check what's new or edited.</summary>
    public async Task TickAsync(bool startup, CancellationToken ct)
    {
        List<EventInfo> events;
        lock (_known)
        {
            events = [.. _known.Values];
        }

        foreach (var e in events)
        {
            await ConsiderAsync(e, startup, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A scheduled event from a gateway dispatch (the same shape as the REST one); null when malformed.</summary>
    internal static EventInfo? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || Snowflake(e, "id") is not { } id || Snowflake(e, "guild_id") is not { } guild)
        {
            return null;
        }

        static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
        static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var start = DateTimeOffset.TryParse(Text(e, "scheduled_start_time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at : DateTimeOffset.MaxValue;
        return new EventInfo(id, guild, Text(e, "name"), Text(e, "description"), Snowflake(e, "channel_id"),
            Int(e, "status"), Int(e, "entity_type"), start, Snowflake(e, "creator_id"));
    }

    private static ulong? Snowflake(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && ulong.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    private async Task ConsiderAsync(EventInfo e, bool startup, CancellationToken ct)
    {
        if (_handled.Contains(e.Id))
        {
            return;
        }

        if (e.EntityType != EventInfo.VoiceEntity || e.ChannelId is not { } channel
            || Screenings.Parse(e.Description) is not { Count: > 0 } lines)
        {
            await InterestAsync(e, false, ct).ConfigureAwait(false); // edited so it's no longer the bot's
            return;
        }

        // Spec 033: "Interested" shows on the event that the bot has picked it up (only events it will run).
        // Spec 038: an event opted out with !event unsubscribe isn't run (and isn't marked).
        var unsubscribed = _settings.IsEventUnsubscribed(e.Id);
        var eligible = e.CreatorId is { } maker && RoleResolver.Satisfies(roles, maker, Role.Friend) && !unsubscribed;
        if (e.Status == EventInfo.Scheduled || (e.Status == EventInfo.Active && !startup))
        {
            await InterestAsync(e, eligible, ct).ConfigureAwait(false);
        }

        if (unsubscribed)
        {
            if (_checked.Remove(e.Id, out var dropped))
            {
                await dropped.Cancel.CancelAsync().ConfigureAwait(false); // no more questions to the creator
                dropped.Cancel.Dispose();
            }

            return;
        }

        var due = e.Status == EventInfo.Scheduled && e.Start <= _time.GetUtcNow();
        var startedByHand = e.Status == EventInfo.Active;
        if (!due && !startedByHand)
        {
            // Spec 031: an upcoming event, new or edited — check its list now, so the creator can fix it ahead of time.
            // Spec 040: a new start time counts as an edit, so questions to the creator stay open until the new start.
            if (e.Status == EventInfo.Scheduled && e.CreatorId is { } author && RoleResolver.Satisfies(roles, author, Role.Friend)
                && !(_checked.TryGetValue(e.Id, out var last) && last.Description == e.Description && last.Start == e.Start))
            {
                if (_checked.Remove(e.Id, out var old))
                {
                    await old.Cancel.CancelAsync().ConfigureAwait(false);
                    old.Cancel.Dispose();
                }

                var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _checked[e.Id] = (e.Description, e.Start, cancel);
                _log.LogInformation("checking the screenings of upcoming event {Event} in guild {Guild}", e.Id, e.GuildId);
                Checks.Add(CheckAsync(Request(e, channel, lines, author), e.Start, cancel.Token));
            }

            return;
        }

        _handled.Add(e.Id);
        if (_checked.Remove(e.Id, out var check))
        {
            await check.Cancel.CancelAsync().ConfigureAwait(false); // the run takes the questions over
            check.Cancel.Dispose();
        }
        if (startedByHand && startup)
        {
            _log.LogInformation("event {Event} in guild {Guild} was already running at startup — leaving it", e.Id, e.GuildId);
            return;
        }

        if (e.CreatorId is not { } creator || !RoleResolver.Satisfies(roles, creator, Role.Friend))
        {
            _log.LogInformation("event {Event} in guild {Guild} has screenings but its creator can't use the bot", e.Id, e.GuildId);
            await SayAsync(channel, $"🙅 **{e.Name}** lists screenings, but whoever made it can't use the bot — not starting them.").ConfigureAwait(false);
            return;
        }

        if (due)
        {
            try
            {
                await source.SetStatusAsync(e.GuildId, e.Id, EventInfo.Active, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "could not start event {Event} in guild {Guild}", e.Id, e.GuildId);
                await SayAsync(channel, "-# Couldn't start the Discord event itself (the bot needs **Manage Events**) — the screenings go ahead anyway.").ConfigureAwait(false);
            }
        }

        _log.LogInformation("starting the screenings of event {Event} ({Count} items) in guild {Guild} channel {Channel}", e.Id, lines.Count, e.GuildId, channel);
        Runs.Add(RunAsync(Request(e, channel, lines, creator), ct));
    }

    private MediaModule.ScreeningRequest Request(EventInfo e, ulong channel, IReadOnlyList<string> lines, ulong creator)
    {
        var self = roles.SelfId;
        return new MediaModule.ScreeningRequest(
            e.GuildId, channel, e.Name, lines, creator,
            m => m.AuthorId != self && RoleResolver.Satisfies(roles, m.AuthorId, Role.Friend),
            () => source.SetStatusAsync(e.GuildId, e.Id, EventInfo.Completed, CancellationToken.None))
        {
            EventId = e.Id,
            OpenDm = async () =>
            {
                try
                {
                    return await source.OpenDmAsync(creator, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "could not open a DM with user {User}", creator);
                    return null;
                }
            },
        };
    }

    private async Task CheckAsync(MediaModule.ScreeningRequest request, DateTimeOffset until, CancellationToken ct)
    {
        try
        {
            await media.CheckScreeningsAsync(request, until, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogError(ex, "checking the screenings of {Name} failed", request.EventName);
        }
    }

    /// <summary>Mark (or unmark) the bot as interested in <paramref name="e"/>, once per change; failures are logged.</summary>
    private async Task InterestAsync(EventInfo e, bool interested, CancellationToken ct)
    {
        if (_interested.Contains(e.Id) == interested)
        {
            return;
        }

        try
        {
            await source.SetInterestedAsync(e.GuildId, e.Id, interested, ct).ConfigureAwait(false);
            _log.LogInformation("{What} interested in event {Event} in guild {Guild}", interested ? "marked" : "no longer", e.Id, e.GuildId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "could not change interest in event {Event} in guild {Guild}", e.Id, e.GuildId);
        }

        // Remembered either way: a refusal is not retried every 30 s.
        if (interested)
        {
            _interested.Add(e.Id);
        }
        else
        {
            _interested.Remove(e.Id);
        }
    }

    private async Task RunAsync(MediaModule.ScreeningRequest request, CancellationToken ct)
    {
        try
        {
            await media.RunScreeningAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogError(ex, "the screenings of {Name} failed", request.EventName);
        }
    }

    private async Task SayAsync(ulong channel, string text)
    {
        try
        {
            await announcer.SendAsync(channel, text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "could not post in channel {Channel}", channel);
        }
    }
}

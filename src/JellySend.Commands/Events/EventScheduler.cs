using JellySend.Commands.Media;
using JellySend.Commands.Routing;
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
    TimeProvider? time = null, ILogger<EventScheduler>? logger = null)
{
    /// <summary>How often events are checked: the most an event can start late.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _log = logger ?? NullLogger<EventScheduler>.Instance;
    private readonly HashSet<ulong> _handled = [];

    /// <summary>Spec 033: events the bot marked itself interested in (it will run them).</summary>
    private readonly HashSet<ulong> _interested = [];

    /// <summary>Spec 031: per upcoming event, the description last checked and that check's cancellation.</summary>
    private readonly Dictionary<ulong, (string Description, CancellationTokenSource Cancel)> _checked = [];

    /// <summary>The screenings started so far (tests await them).</summary>
    internal List<Task> Runs { get; } = [];

    /// <summary>The ahead-of-time checks started so far (spec 031; tests await them).</summary>
    internal List<Task> Checks { get; } = [];

    /// <summary>Poll until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var startup = true;
        while (!ct.IsCancellationRequested)
        {
            await PollAsync(startup, ct).ConfigureAwait(false);
            startup = false;
            try
            {
                await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One check of every server's events. On the first (<paramref name="startup"/>), events already running are left
    /// alone: they were started before this process (a restart must not start the screenings again).
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

            foreach (var e in events)
            {
                await ConsiderAsync(e, startup, ct).ConfigureAwait(false);
            }
        }
    }

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
        var eligible = e.CreatorId is { } maker && RoleResolver.Satisfies(roles, maker, Role.Friend);
        if (e.Status == EventInfo.Scheduled || (e.Status == EventInfo.Active && !startup))
        {
            await InterestAsync(e, eligible, ct).ConfigureAwait(false);
        }

        var due = e.Status == EventInfo.Scheduled && e.Start <= _time.GetUtcNow();
        var startedByHand = e.Status == EventInfo.Active;
        if (!due && !startedByHand)
        {
            // Spec 031: an upcoming event, new or edited — check its list now, so the creator can fix it ahead of time.
            if (e.Status == EventInfo.Scheduled && e.CreatorId is { } author && RoleResolver.Satisfies(roles, author, Role.Friend)
                && !(_checked.TryGetValue(e.Id, out var last) && last.Description == e.Description))
            {
                if (_checked.Remove(e.Id, out var old))
                {
                    await old.Cancel.CancelAsync().ConfigureAwait(false);
                    old.Cancel.Dispose();
                }

                var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _checked[e.Id] = (e.Description, cancel);
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

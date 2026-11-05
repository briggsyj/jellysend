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

    /// <summary>The screenings started so far (tests await them).</summary>
    internal List<Task> Runs { get; } = [];

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
        if (_handled.Contains(e.Id) || e.EntityType != EventInfo.VoiceEntity || e.ChannelId is not { } channel
            || Screenings.Parse(e.Description) is not { Count: > 0 } lines)
        {
            return;
        }

        var due = e.Status == EventInfo.Scheduled && e.Start <= _time.GetUtcNow();
        var startedByHand = e.Status == EventInfo.Active;
        if (!due && !startedByHand)
        {
            return;
        }

        _handled.Add(e.Id);
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
        var self = roles.SelfId;
        var request = new MediaModule.ScreeningRequest(
            e.GuildId, channel, e.Name, lines, creator,
            m => m.AuthorId != self && RoleResolver.Satisfies(roles, m.AuthorId, Role.Friend),
            () => source.SetStatusAsync(e.GuildId, e.Id, EventInfo.Completed, CancellationToken.None));
        Runs.Add(RunAsync(request, ct));
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

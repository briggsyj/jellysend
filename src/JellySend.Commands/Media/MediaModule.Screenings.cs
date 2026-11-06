using System.Globalization;
using JellySend.Commands.Routing;
using JellySend.Services.Jellyfin;
using Microsoft.Extensions.Logging;

namespace JellySend.Commands.Media;

/// <summary>
/// Spec 028: a Discord event's screenings. The event takes over the server's stream in the event's voice channel. Each
/// line is resolved like <c>!play</c>: a single match is used straight away, several are asked about in the channel's
/// chat (anyone allowed to <c>!play</c> answers, until shortly before that item is due; unanswered, the first is used).
/// The schedule (start of each item, and the end) is posted in that chat, and again after every pick. When the last
/// screening ends, the event is ended.
/// </summary>
public sealed partial class MediaModule
{
    /// <summary>The shortest wait for a pick, even when the item is due sooner (it is the first, or picks ran late).</summary>
    public static readonly TimeSpan MinPickWait = TimeSpan.FromMinutes(2);

    /// <summary>How long before an item is due its pick closes, so it can be queued in time.</summary>
    public static readonly TimeSpan PickLead = TimeSpan.FromMinutes(1);

    /// <summary>Posts to channels outside a command (the event's voice channel chat). Set by the host.</summary>
    public IMessageSender? Announcer { get; init; }

    private MessageHub? _hub;

    /// <summary>A screening request from the event scheduler.</summary>
    /// <param name="MayAnswer">Who may answer a pick in the channel's chat.</param>
    /// <param name="EndEvent">Ends the Discord event (after the last screening).</param>
    public sealed record ScreeningRequest(
        ulong Guild, ulong Channel, string EventName, IReadOnlyList<string> Lines, ulong CreatorId,
        Func<ChatMessage, bool> MayAnswer, Func<Task> EndEvent);

    private sealed class Slot(string line)
    {
        public string Line { get; } = line;

        /// <summary>Resolved items (a range gives several); null while still to be picked.</summary>
        public List<MediaItem>? Items { get; set; }

        public bool Missing { get; set; }

        public bool Settled => Items is not null || Missing;
    }

    private sealed class ScreeningRun(ScreeningRequest request, List<Slot> slots)
    {
        public ScreeningRequest Request { get; } = request;

        public List<Slot> Slots { get; } = slots;

        /// <summary>Slots before this index are in the playlist (or were skipped).</summary>
        public int Queued { get; set; }

        /// <summary>Screening items queued but not started yet.</summary>
        public HashSet<MediaItem> Waiting { get; } = new(ReferenceEqualityComparer.Instance);

        public DateTimeOffset? StartedAt { get; set; }

        public bool Ended { get; set; }

        /// <summary>
        /// Spec 030: someone changed the playlist (play, add, remove, order, skip) — the bot is under manual control:
        /// the empty-channel timeout applies again, and it doesn't sign off after the last screening.
        /// </summary>
        public bool Manual { get; set; }

        public bool AllSettled => Slots.TrueForAll(s => s.Settled);
    }

    /// <summary>Run an event's screenings (spec 028). Returns once every pick is settled.</summary>
    public async Task RunScreeningAsync(ScreeningRequest request, CancellationToken ct)
    {
        if (_library is null || _hub is null || Announcer is null)
        {
            _log.LogWarning("screenings for event {Name} skipped: Jellyfin or the announcer isn't set up", request.EventName);
            return;
        }

        Task Say(string text) => Announcer.SendAsync(request.Channel, text, CancellationToken.None);
        await Say($"🎬 **{Shorten(request.EventName, 80)}** — the screenings are starting! Tuning in…").ConfigureAwait(false);

        // Pass 1, quietly: everything with a single match. A title that would show a picker is asked about later.
        var slots = new List<Slot>();
        foreach (var line in request.Lines)
        {
            var slot = new Slot(line);
            var quiet = new CommandContext(new ChatMessage(request.Guild, request.Channel, request.CreatorId, line), "", Silent.Instance, _hub, ct)
            {
                PromptTimeout = TimeSpan.Zero,
                Answerers = _ => false,
            };
            var more = new List<MediaItem>();
            var (item, _) = await ResolveWithFallbackAsync(quiet, PlayQuery.Parse(line, withTime: false), more).ConfigureAwait(false);
            if (item is not null)
            {
                slot.Items = [item, .. more];
            }
            else if (!quiet.Prompts.Shown)
            {
                slot.Missing = true;
            }

            slots.Add(slot);
        }

        // The event takes over: stop what's on, keep the rest of the playlist behind the screenings.
        await StopStreamAsync(request.Guild).ConfigureAwait(false);
        var state = State(request.Guild, Say);
        var run = new ScreeningRun(request, slots);
        state.Screening = run;
        foreach (var missing in slots.Where(s => s.Missing))
        {
            await Say($"🔎 `{Shorten(missing.Line)}` isn't in the library — skipped.").ConfigureAwait(false);
        }

        await QueueSettledAsync(state, run).ConfigureAwait(false);

        // Pass 2, in order: ask about each title with several matches, until shortly before it's due.
        foreach (var slot in slots.Where(s => !s.Settled).ToList())
        {
            if (!StillRunning(state, run))
            {
                return;
            }

            var due = DueTime(run, slot);
            var wait = due is { } d ? d - PickLead - _time.GetUtcNow() : MinPickWait;
            var context = new CommandContext(new ChatMessage(request.Guild, request.Channel, request.CreatorId, slot.Line), "", Announcer, _hub, ct)
            {
                PromptTimeout = wait < MinPickWait ? MinPickWait : wait,
                PickFirstOnTimeout = true,
                Answerers = m => m.ChannelId == request.Channel && request.MayAnswer(m),
            };
            await Say($"🎬 Screening {slots.IndexOf(slot) + 1}, `{Shorten(slot.Line)}`, needs a pick — anyone can answer below.").ConfigureAwait(false);
            var more = new List<MediaItem>();
            var (item, _) = await ResolveWithFallbackAsync(context, PlayQuery.Parse(slot.Line, withTime: false), more).ConfigureAwait(false);
            if (item is not null)
            {
                slot.Items = [item, .. more];
            }
            else
            {
                slot.Missing = true;
                await Say($"🎬 Screening {slots.IndexOf(slot) + 1} skipped.").ConfigureAwait(false);
            }

            if (StillRunning(state, run))
            {
                await QueueSettledAsync(state, run).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The run is still this server's (not left, not replaced by another event).</summary>
    private bool StillRunning(GuildState state, ScreeningRun run) =>
        _states.TryGetValue(run.Request.Guild, out var current) && ReferenceEquals(current, state) && ReferenceEquals(state.Screening, run);

    /// <summary>
    /// Queue every settled slot from <see cref="ScreeningRun.Queued"/> up to the first still to be picked, ahead of the
    /// rest of the playlist; start them when nothing's on; then post the schedule.
    /// </summary>
    private async Task QueueSettledAsync(GuildState state, ScreeningRun run)
    {
        var guild = run.Request.Guild;
        await state.StartLock.WaitAsync().ConfigureAwait(false);
        try
        {
            while (run.Queued < run.Slots.Count && run.Slots[run.Queued].Settled)
            {
                foreach (var item in run.Slots[run.Queued].Items ?? [])
                {
                    var after = state.Queue.LastIndexOf(run.Waiting.Contains);
                    state.Queue.InsertAt(after + 1, item);
                    run.Waiting.Add(item);
                }

                run.Queued++;
            }

            if (state.Now is null && run.Waiting.Count > 0)
            {
                await _voice.EnsureAsync(guild, run.Request.Channel, CancellationToken.None).ConfigureAwait(false);
                run.StartedAt ??= _time.GetUtcNow();
                if (await AdvanceHoldingLockAsync(guild, state).ConfigureAwait(false))
                {
                    StartPlaytimeLimit(guild);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "screenings in guild {Guild} failed to start", guild);
            await state.Send("❌ We couldn't start the screenings. Try `!play` — if it keeps happening, ask an admin to check the logs.").ConfigureAwait(false);
        }
        finally
        {
            state.StartLock.Release();
        }

        if (run.StartedAt is not null || run.Waiting.Count > 0)
        {
            await state.Send(ScheduleText(run)).ConfigureAwait(false);
        }
    }

    /// <summary>When <paramref name="slot"/> is due: the start plus every earlier settled item's runtime; null before the start.</summary>
    private static DateTimeOffset? DueTime(ScreeningRun run, Slot slot)
    {
        if (run.StartedAt is not { } at)
        {
            return null;
        }

        foreach (var earlier in run.Slots.TakeWhile(s => !ReferenceEquals(s, slot)))
        {
            at += TimeSpan.FromSeconds((earlier.Items ?? []).Sum(i => i.RuntimeSeconds ?? 0));
        }

        return at;
    }

    /// <summary>
    /// The schedule, in Discord timestamps (each reader sees their own time zone): each item's start, then the end. Times
    /// after a title still to be picked are not known yet.
    /// </summary>
    private static string ScheduleText(ScreeningRun run)
    {
        var lines = new List<string> { $"🎬 **{Shorten(run.Request.EventName, 80)}** — screening times (in your time zone):" };
        var at = run.StartedAt;
        var n = 0;
        foreach (var slot in run.Slots)
        {
            if (slot.Missing)
            {
                continue;
            }

            if (slot.Items is not { } items)
            {
                lines.Add($"`{++n}` ⏳ `{Shorten(slot.Line)}` — waiting for a pick");
                at = null;
                continue;
            }

            foreach (var item in items)
            {
                lines.Add($"`{++n}` {(at is { } t ? Stamp(t) : "—")} {MediaLabels.Format(item)}");
                at = at is { } t2 && item.RuntimeSeconds is { } seconds ? t2 + TimeSpan.FromSeconds(seconds) : null;
            }
        }

        lines.Add(at is { } end ? $"🏁 Ends around {Stamp(end)}" : "🏁 End time to follow, once every pick is made.");
        var text = string.Join('\n', lines);
        return text.Length <= CommandCatalog.MessageLimit ? text : text[..CommandCatalog.MessageLimit] + "…";
    }

    private static string Stamp(DateTimeOffset t) =>
        string.Create(CultureInfo.InvariantCulture, $"<t:{t.ToUnixTimeSeconds()}:t>");

    /// <summary>Spec 030: an untouched event run holds the channel (no empty-channel timeout).</summary>
    public bool HoldsChannel(ulong guildId) =>
        _states.TryGetValue(guildId, out var state) && state.Screening is { Ended: false, Manual: false };

    /// <summary>
    /// Spec 030: a user changed what plays during an event — from now on the bot is under manual control: the
    /// empty-channel timeout applies (starting now if nobody's there), and it won't sign off after the last screening.
    /// </summary>
    private async Task MarkManualAsync(ulong guild, string what)
    {
        if (!_states.TryGetValue(guild, out var state) || state.Screening is not { Ended: false, Manual: false } run)
        {
            return;
        }

        run.Manual = true;
        _log.LogInformation("event screenings in guild {Guild} are now under manual control ({What})", guild, what);
        var timeout = _voiceModule?.EmptyChannelTimeout ?? TimeSpan.Zero;
        await state.Send(timeout > TimeSpan.Zero
            ? $"🎛️ The playlist's been changed, so you're at the controls now — the bot signs off if the channel stays empty for {timeout.TotalMinutes:0} minutes."
            : "🎛️ The playlist's been changed, so you're at the controls now.").ConfigureAwait(false);
        if (_voiceModule is { } voiceModule)
        {
            await voiceModule.WatchIfEmptyAsync(guild).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Called before each advance (spec 028) with the item about to play: marks a screening as started; once the last
    /// one has ended, ends the Discord event. Returns true when the bot should stop here and sign off (spec 030: the
    /// screenings ran untouched to the end).
    /// </summary>
    private async Task<bool> ScreeningAdvanceAsync(GuildState state, MediaItem? next)
    {
        if (state.Screening is not { Ended: false } run)
        {
            return false;
        }

        if (next is not null && run.Waiting.Remove(next))
        {
            return false;
        }

        if (run.Waiting.Count > 0 || !run.AllSettled || run.Queued != run.Slots.Count)
        {
            return false;
        }

        run.Ended = true;
        state.Screening = null;
        await state.Send(run.Manual
            ? "🎬 That's all the screenings — thanks for watching!"
            : "🎬 That's all the screenings — thanks for watching! 📴 Signing off.").ConfigureAwait(false);
        try
        {
            await run.Request.EndEvent().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "could not end the Discord event in guild {Guild}", run.Request.Guild);
        }

        return !run.Manual;
    }

    /// <summary>Spec 030: the untouched screenings are over — stop, drop the session and leave the channel.</summary>
    private async Task SignOffAfterScreeningsAsync(ulong guild)
    {
        _log.LogInformation("event screenings in guild {Guild} finished untouched — signing off", guild);
        await StopAsync(guild, "screenings-ended").ConfigureAwait(false);
        await _voice.LeaveAsync(guild).ConfigureAwait(false);
    }

    /// <summary>Swallows messages (the quiet first pass).</summary>
    private sealed class Silent : IMessageSender
    {
        public static readonly Silent Instance = new();

        public Task SendAsync(ulong channelId, string content, CancellationToken ct) => Task.CompletedTask;
    }
}

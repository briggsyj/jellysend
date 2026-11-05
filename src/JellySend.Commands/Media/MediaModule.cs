using System.Collections.Concurrent;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using JellySend.Common.Logging;
using JellySend.Common.Permissions;
using JellySend.Commands.Interaction;
using JellySend.Commands.Routing;
using JellySend.Commands.Voice;
using JellySend.Services.Jellyfin;
using JellySend.Services.SyncPlay;
using JellySend.Streaming.Engine;

namespace JellySend.Commands.Media;

/// <summary>
/// <c>!media</c> (<c>cogs/jellyfin.py</c>): search → pickers → go-live, playback control, playlist with
/// auto-advance, aspect ratio, SyncPlay group sync, and the .NET-only debug overlay. Also the
/// <see cref="IStreamControl"/> the voice commands stop streams through.
/// </summary>
public sealed class MediaModule : IStreamControl
{
    /// <summary>Codecs the pipeline can transcode from.</summary>
    public static readonly IReadOnlySet<string> SupportedCodecs = new HashSet<string>(["h264", "hevc", "mpeg2video", "mpeg4", "mjpeg"]);

    /// <summary>How far a SyncPlay position may drift from ours before we seek.</summary>
    public static readonly TimeSpan SyncDriftTolerance = TimeSpan.FromSeconds(3);

    /// <summary>Ignore PlayQueue echoes of items we queued ourselves this recently.</summary>
    public static readonly TimeSpan SyncEchoWindow = TimeSpan.FromSeconds(10);

    /// <summary>After our own pause/unpause/seek, trust our position over the echoed command's.</summary>
    public static readonly TimeSpan SyncOwnCommandWindow = TimeSpan.FromSeconds(5);

    /// <summary>How many times one item is resumed after its screenshare connection dropped.</summary>
    public const int MaxReconnects = 3;

    /// <summary>How long the SyncPlay queue waits for the first frame before using the requested start.</summary>
    public static readonly TimeSpan FirstFrameWait = TimeSpan.FromSeconds(60);

    private const string NothingOnAir = "🙅 Nothing's on air right now.";

    /// <summary>A retry or reconnect that itself blew up (spec 020: never leave "trying…" hanging).</summary>
    internal const string RecoveryFailed = "❌ That didn't work either. Try `!play` again — if it keeps happening, ask an admin to check the logs.";

    /// <summary>The screenshare dropped, nothing resumed it, and the playlist carries on with the next item.</summary>
    internal const string LostSignalNext = "📴 We've lost the signal — the screenshare dropped. Moving on to the next on the playlist.";

    private const string NotConfigured =
        "❌ The Jellyfin library isn't set up. An admin needs to set `JELLYFIN_URL` and `JELLYFIN_API_KEY` (or `JELLYFIN_USERNAME` / "
        + "`JELLYFIN_PASSWORD`) in `.env` and restart the bot.";

    private readonly IMediaLibrary? _library;
    private readonly StreamEngine _engine;

    /// <summary>The voice commands (set by <see cref="Register"/>): the empty-channel countdown after a fallback join.</summary>
    private VoiceModule? _voiceModule;
    private readonly VoiceManager _voice;
    private readonly Func<ulong, string?> _guildName;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<ulong, GuildState> _states = new();

    // Preferences: aspect survives leaving voice; debug is cleared on leave.
    private readonly ConcurrentDictionary<ulong, double> _aspect = new();

    /// <summary>
    /// Items whose original file failed to start as a direct stream (spec 012): they always go through Jellyfin's
    /// transcode from then on (until the bot restarts).
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _directFailed = new();

    /// <summary>Per guild: output height chosen with <c>!resolution</c> (spec 010); absent = the configured one.</summary>
    private readonly ConcurrentDictionary<ulong, int> _height = new();
    private readonly ConcurrentDictionary<ulong, bool> _debug = new();

    // Spec 005: remembered subtitle/audio languages per guild; cleared on leave.
    private readonly ConcurrentDictionary<ulong, TrackPrefs> _trackPrefs = new();

    public MediaModule(
        IMediaLibrary? library,
        StreamEngine engine,
        VoiceManager voice,
        Func<ulong, string?>? guildName = null,
        TimeProvider? time = null,
        ILogger<MediaModule>? logger = null)
    {
        _library = library;
        _engine = engine;
        _voice = voice;
        _guildName = guildName ?? (_ => null);
        _time = time ?? TimeProvider.System;
        _log = logger ?? NullLogger<MediaModule>.Instance;
    }

    /// <summary>
    /// Longest a stream plays (spec 016, <c>MAX_STREAM_HOURS</c>): counted from the user's start (<c>!play</c>,
    /// <c>!queue</c> when idle) through auto-advance, skips and restarts; then the stream stops (voice and playlist
    /// stay). Zero or negative = no limit.
    /// </summary>
    public TimeSpan MaxPlaytime { get; init; } = TimeSpan.FromHours(12);

    /// <summary>The running playtime limit per guild — awaited by tests.</summary>
    internal ConcurrentDictionary<ulong, (Task Task, CancellationTokenSource Cancel)> PlaytimeLimits { get; } = new();

    /// <summary>Background work (SyncPlay queue sync, auto-advance) — awaited by tests.</summary>
    internal ConcurrentDictionary<Task, byte> Background { get; } = new();

    /// <summary>
    /// Register every command twice (spec 008): as <c>!media &lt;name&gt;</c> and as the shortcut <c>!&lt;name&gt;</c>, with
    /// the same aliases and role. <c>!media</c> on its own (or with an unknown word) shows the help.
    /// </summary>
    public void Register(CommandRouter router, VoiceModule voice)
    {
        _voiceModule = voice;
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            ["play"] = PlayAsync,
            ["pause"] = PauseAsync,
            ["time"] = TimeAsync,
            ["skip"] = SkipAsync,
            ["stop"] = voice.StopAsync,
            ["leave"] = voice.LeaveAsync,
            ["join"] = voice.JoinAsync,
            ["playlist"] = PlaylistAsync,
            ["subtitles"] = SubtitlesAsync,
            ["audio"] = AudioTrackAsync,
            ["language"] = LanguageAsync,
            ["aspect-ratio"] = AspectAsync,
            ["resolution"] = ResolutionAsync,
            ["debug"] = DebugAsync,
            ["help"] = SendHelpAsync,
            ["ping"] = PingAsync,
        };

        var media = router.Add("media", Role.Friend, MediaGroupAsync);
        foreach (var info in CommandCatalog.All)
        {
            var run = handlers[info.Name];
            media.Sub(info.Name, info.Role, run, info.Aliases);
            router.Add(info.Name, info.Role, run, info.Aliases);
        }

        // Spec 022: hidden phrases for some commands, each with an extra themed reply first.
        foreach (var (phrase, command, reply) in SecretCatalog.All())
        {
            router.AddSecret(phrase, command, reply);
        }

        // Spec 021: the old playlist commands still work, with a pointer to their new name.
        foreach (var (old, action) in new[] { ("queue", "add"), ("order", "order"), ("remove", "remove") })
        {
            CommandHandler legacy = async c =>
            {
                await c.ReplyAsync($"-# `!{old}` is now `!playlist {action}`.").ConfigureAwait(false);
                await PlaylistAsync(c.WithRest($"{action} {c.Rest}".Trim())).ConfigureAwait(false);
            };
            media.Sub(old, Role.Friend, legacy);
            router.Add(old, Role.Friend, legacy);
        }
    }

    /// <summary>User text echoed in a reply, cut to <paramref name="max"/> characters so the reply stays within Discord's limit.</summary>
    internal static string Shorten(string text, int max = 100) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private static Task PingAsync(CommandContext c) => c.ReplyAsync("🏓 Pong! Still broadcasting.");

    // ── IStreamControl (voice commands) ──────────────────────────────────────

    /// <summary>A stream is running, or an item is being started (so !stop can cancel a start in progress).</summary>
    public bool IsStreaming(ulong guildId) => _engine.IsStreaming(guildId) || IsStarting(guildId);

    public bool IsStarting(ulong guildId) => _states.TryGetValue(guildId, out var s) && s.StartLock.CurrentCount == 0;

    public async Task StopAsync(ulong guildId, string reason)
    {
        try
        {
            await StopStreamAsync(guildId).ConfigureAwait(false);
            if (reason != "stop")
            {
                // The bot leaves voice: drop the session (playlist, SyncPlay) and the debug overlay.
                await TeardownAsync(guildId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "error stopping media in guild {Guild} ({Reason})", guildId, reason);
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>!media</c> without a known sub-command: the help, after a hint when the words were an old command
    /// (<c>!media &lt;title&gt;</c>, <c>!media search</c>, <c>!media resume</c>).
    /// </summary>
    private async Task MediaGroupAsync(CommandContext c)
    {
        var (word, _) = CommandRouter.SplitFirst(c.Rest);
        var hint = word.ToLowerInvariant() switch
        {
            "" => null,
            "resume" => "-# `!media resume` is gone: `!play` resumes, `!time <time>` jumps\n\n",
            "search" => "-# `!media search` is gone: use `!play <title>`\n\n",
            _ => $"-# `!media <title>` is now `!play <title>` — e.g. `!play {Shorten(c.Rest.Trim(), 80)}`\n\n",
        };
        await SendHelpTextAsync(c, hint).ConfigureAwait(false);
    }

    /// <summary>
    /// Play what <paramref name="query"/> names (spec 008): search its title (an episode or a season when given), then
    /// start at its time. When the recognised formats find nothing, the whole text is searched as a title instead
    /// (<c>!play 8 to 4</c>, a title that looks like a time), unless the title was quoted.
    /// </summary>
    private async Task PlayQueryAsync(CommandContext c, PlayQuery query)
    {
        if (!await RequireLibraryAsync(c).ConfigureAwait(false))
        {
            return;
        }

        var more = new List<MediaItem>();
        var (item, fellBack) = await ResolveWithFallbackAsync(c, query, more).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        var start = fellBack ? 0 : query.Start ?? 0;
        if (item.RuntimeSeconds is { } runtime && start >= runtime)
        {
            await c.ReplyAsync($"🙅 {MediaText.FormatTimestamp(start)} is past the end ({MediaText.FormatTimestamp(runtime)}).").ConfigureAwait(false);
            return;
        }

        if (start > 0)
        {
            await c.ReplyAsync($"⏩ Starting at {MediaText.FormatTimestamp(start)}").ConfigureAwait(false);
        }

        if (await PlayItemAsync(c, item, start).ConfigureAwait(false) && more.Count > 0 && ContextGuild(c) is { } guild
            && _states.TryGetValue(guild, out var state))
        {
            // Spec 014: the other picked episodes play right after this one, in the order picked.
            var queued = state.Queue.InsertNext(more);
            await c.ReplyAsync(QueuedText(more, queued, "up next")).ConfigureAwait(false);
        }
    }

    /// <summary>The reply for several queued episodes: what was queued, and how many did not fit.</summary>
    private static string QueuedText(IReadOnlyList<MediaItem> items, int queued, string where)
    {
        var names = string.Join(", ", items.Take(queued).Select(i => $"**{MediaLabels.Format(i)}**"));
        var text = queued > 0 ? $"➕ Added to the playlist{(where.Length > 0 ? " " + where : "")}: {names}" : "";
        if (queued < items.Count)
        {
            text += (text.Length > 0 ? "\n" : "") + $"🙅 The playlist is full ({MediaText.PlaylistMax} items) — {items.Count - queued} not added.";
        }

        return text;
    }

    /// <summary>Search <paramref name="query"/>; if its formats found nothing at all, search the whole text as a title.</summary>
    /// <param name="more">Collects further episodes picked together with the first (<c>2,5,8</c>; spec 014); null = one pick.</param>
    private async Task<(MediaItem? Item, bool FellBack)> ResolveWithFallbackAsync(CommandContext c, PlayQuery query, List<MediaItem>? more = null)
    {
        var plain = query.AsPlainTitle();
        var canFallBack = !query.Quoted && (query.HasFormats || query.Title.Length == 0) && plain.Title.Length > 0
            && !string.Equals(plain.Title, query.Title, StringComparison.Ordinal);
        if (query.Title.Length == 0)
        {
            return (await ResolveAsync(c, plain, more: more).ConfigureAwait(false), true); // `!play s01e02`: only formats, so a title
        }

        var quiet = canFallBack ? new QuietMiss() : null;
        var item = await ResolveAsync(c, query, quiet, more).ConfigureAwait(false);
        if (item is not null || quiet is not { NothingFound: true })
        {
            return (item, false); // found, or cancelled/failed in a picker or on an error (already replied)
        }

        // Neither found anything: report the miss as asked (`S01E02 of `show` not found`), not the whole-text search.
        var second = new QuietMiss();
        var fallback = await ResolveAsync(c, plain, second, more).ConfigureAwait(false);
        if (fallback is null && second.NothingFound)
        {
            await c.ReplyAsync(NotFoundText(query)).ConfigureAwait(false);
        }

        return (fallback, true);
    }

    private static string NotFoundText(PlayQuery query) => query.Spans is { } spans
        ? $"🔎 {string.Join(", ", spans)} of `{Shorten(query.Title)}` isn't in the library."
        : query is { Season: { } s, Episode: { } e }
        ? $"🔎 S{s:00}E{e:00} of `{Shorten(query.Title)}` isn't in the library."
        : $"🔎 Nothing found for `{Shorten(query.Text)}` — check the spelling?";

    /// <summary>Lets the first, formats-applied search of a fall-back pair skip its "nothing found" reply.</summary>
    private sealed class QuietMiss
    {
        public bool NothingFound { get; set; }
    }

    /// <summary><c>!help</c> — all commands; <c>!help &lt;command&gt;</c> — that one.</summary>
    private async Task SendHelpAsync(CommandContext c)
    {
        var arg = c.Rest.Trim();
        if (arg.Length == 0)
        {
            await SendHelpTextAsync(c, null).ConfigureAwait(false);
            return;
        }

        if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            // Spec 019: every command in detail, a message per section (Discord's 2000-character limit).
            foreach (var message in CommandCatalog.FullGuide())
            {
                await c.ReplyAsync(message).ConfigureAwait(false);
            }

            return;
        }

        await c.ReplyAsync(CommandCatalog.Find(arg) is { } info
            ? info.HelpText()
            : $"🙅 No command called `{Shorten(arg.TrimStart('!'))}` — `!help` lists them all.").ConfigureAwait(false);
    }

    private async Task SendHelpTextAsync(CommandContext c, string? prefix)
    {
        var text = (prefix ?? "") + MediaText.HelpText;
        if (_library is { HasUserCredentials: true })
        {
            var guild = ContextGuild(c);
            var name = guild is { } g ? _guildName(g) : null;
            text += string.Format(System.Globalization.CultureInfo.InvariantCulture, MediaText.SyncPlayHelp, SyncPlayGroup.GroupNameFor(name ?? "<server name>"));
        }

        await c.ReplyAsync(text).ConfigureAwait(false);
    }

    private async Task PauseAsync(CommandContext c)
    {
        var guild = ContextGuild(c);
        var player = guild is { } g ? _engine.GetPlayback(g) : null;
        if (guild is null || player is null)
        {
            await c.ReplyAsync(NothingOnAir).ConfigureAwait(false);
            return;
        }

        if (player.IsPaused)
        {
            await c.ReplyAsync("⏸ Already paused — `!play` to carry on.").ConfigureAwait(false);
            return;
        }

        player.Pause();
        await c.ReplyAsync($"⏸ Paused at {MediaText.FormatTimestamp(player.Position)} — we'll be right back. `!play` to carry on.").ConfigureAwait(false);
        await SyncCallAsync(guild.Value, (s, ct) => s.PauseAsync(ct), "pause").ConfigureAwait(false);
    }

    /// <summary>
    /// <c>!play</c>: bare — resume, or start the playlist; a time alone while something plays — jump to it;
    /// anything else — search and play (spec 008).
    /// </summary>
    private async Task PlayAsync(CommandContext c)
    {
        if (c.Rest.Length == 0)
        {
            await PlayOrStartPlaylistAsync(c).ConfigureAwait(false);
            return;
        }

        var query = PlayQuery.Parse(c.Rest);
        if (query is { Title.Length: 0, Season: null, Start: { } jump } && ContextGuild(c) is { } guild && _engine.GetPlayback(guild) is { } player)
        {
            await JumpAsync(c, guild, player, jump).ConfigureAwait(false);
            return;
        }

        await PlayQueryAsync(c, query).ConfigureAwait(false);
    }

    private async Task PlayOrStartPlaylistAsync(CommandContext c)
    {
        var guild = ContextGuild(c);
        var player = guild is { } g ? _engine.GetPlayback(g) : null;
        if (guild is not null && player is not null)
        {
            if (!player.IsPaused)
            {
                await c.ReplyAsync("▶️ Already on air.").ConfigureAwait(false);
                return;
            }

            player.Resume();
            await c.ReplyAsync($"▶️ Back on air at {MediaText.FormatTimestamp(player.Position)}").ConfigureAwait(false);
            await SyncCallAsync(guild.Value, (s, ct) => s.UnpauseAsync(ct), "unpause").ConfigureAwait(false);
            return;
        }

        // Nothing on screen: start the playlist if there is one.
        if (guild is { } id && _states.TryGetValue(id, out var state) && state.Queue.Count > 0 && _library is not null)
        {
            var item = state.Queue.TakeNext()!;
            if (!await PlayItemAsync(c, item).ConfigureAwait(false))
            {
                state.Queue.PushFront(item);
            }

            return;
        }

        await c.ReplyAsync("🙅 Nothing's paused — `!play <title>` to put something on.").ConfigureAwait(false);
    }

    /// <summary><c>!time</c> — where the stream is; <c>!time &lt;time&gt;</c> — jump there.</summary>
    private async Task TimeAsync(CommandContext c)
    {
        var text = c.Rest;
        var seconds = text.Length > 0 ? MediaText.ParseTimestamp(text) : null;
        if (text.Length > 0 && seconds is null)
        {
            await c.ReplyAsync($"🙅 `{Shorten(text)}` isn't a time — try `1:23:45`, `1.23.45`, `1:23` (h:mm), `45m` or `90s`.").ConfigureAwait(false);
            return;
        }

        var guild = ContextGuild(c);
        var player = guild is { } g ? _engine.GetPlayback(g) : null;
        if (guild is null || player is null)
        {
            await c.ReplyAsync(NothingOnAir).ConfigureAwait(false);
            return;
        }

        if (seconds is null)
        {
            var runtime = _states.TryGetValue(guild.Value, out var now) ? now.Now?.Item.RuntimeSeconds : null;
            var total = runtime is { } rt ? $" / {MediaText.FormatTimestamp(rt)}" : "";
            await c.ReplyAsync($"⏱ {MediaText.FormatTimestamp(player.Position)}{total}{(player.IsPaused ? " (paused)" : "")}").ConfigureAwait(false);
            return;
        }

        await JumpAsync(c, guild.Value, player, seconds.Value).ConfigureAwait(false);
    }

    private async Task JumpAsync(CommandContext c, ulong guild, IPlayback player, double seconds)
    {
        var runtime = _states.TryGetValue(guild, out var state) ? state.Now?.Item.RuntimeSeconds : null;
        if (runtime is { } total && seconds >= total)
        {
            await c.ReplyAsync($"🙅 {MediaText.FormatTimestamp(seconds)} is past the end ({MediaText.FormatTimestamp(total)}).").ConfigureAwait(false);
            return;
        }

        var wasPaused = player.IsPaused;
        player.Seek(seconds);
        player.Resume();
        await c.ReplyAsync($"⏩ Jumping to {MediaText.FormatTimestamp(seconds)}").ConfigureAwait(false);
        await SyncCallAsync(guild, (s, ct) => s.SeekAsync(seconds, ct), "seek").ConfigureAwait(false);
        if (wasPaused)
        {
            await SyncCallAsync(guild, (s, ct) => s.UnpauseAsync(ct), "unpause").ConfigureAwait(false);
        }
    }

    private async Task SkipAsync(CommandContext c)
    {
        var guild = ContextGuild(c);
        GuildState? state = null;
        if (guild is null || !_states.TryGetValue(guild.Value, out state) || (state.Queue.Count == 0 && state.Now is null))
        {
            await c.ReplyAsync("🙅 Nothing to skip.").ConfigureAwait(false);
            return;
        }

        if (state.Queue.Count == 0)
        {
            await StopStreamAsync(guild.Value).ConfigureAwait(false);
            await c.ReplyAsync("⏹ That's the end of the playlist — off air.").ConfigureAwait(false);
            return;
        }

        state.Send = c.ReplyAsync;
        if (_voice.Get(guild.Value) is null)
        {
            var item = state.Queue.TakeNext()!;
            if (!await PlayItemAsync(c, item).ConfigureAwait(false))
            {
                state.Queue.PushFront(item);
            }

            return;
        }

        await AdvanceAsync(guild.Value).ConfigureAwait(false);
    }

    private async Task QueueAsync(CommandContext c)
    {
        // Spec 002 FR-013: queued items start from the beginning — a time-like word stays part of the title.
        var query = PlayQuery.Parse(c.Rest, withTime: false);
        if (!await RequireLibraryAsync(c).ConfigureAwait(false))
        {
            return;
        }

        var guild = ContextGuild(c);
        if (guild is null)
        {
            await c.ReplyAsync("🙅 Join a voice channel first, then try again.").ConfigureAwait(false);
            return;
        }

        var state = State(guild.Value, c.ReplyAsync);
        if (state.Queue.IsFull)
        {
            await c.ReplyAsync($"🙅 The playlist is full ({MediaText.PlaylistMax} items).").ConfigureAwait(false);
            return;
        }

        var more = new List<MediaItem>();
        var (item, _) = await ResolveWithFallbackAsync(c, query, more).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        if (_engine.GetPlayback(guild.Value) is null && state.StartLock.CurrentCount > 0)
        {
            // Idle: play now — or, if a playlist is waiting, queue this and start the playlist from its head.
            if (state.Queue.Count == 0)
            {
                if (await PlayItemAsync(c, item).ConfigureAwait(false) && more.Count > 0)
                {
                    await c.ReplyAsync(QueuedText(more, AppendAll(state, more), "up next")).ConfigureAwait(false);
                }

                return;
            }

            if (state.Queue.Add(item) is null)
            {
                await c.ReplyAsync($"🙅 The playlist is full ({MediaText.PlaylistMax} items).").ConfigureAwait(false);
                return;
            }

            var appended = AppendAll(state, more);
            var head = state.Queue.TakeNext()!;
            await c.ReplyAsync(more.Count == 0
                ? $"➕ Added to the playlist: **{MediaLabels.Format(item)}** (#{state.Queue.Count})"
                : QueuedText([item, .. more], appended + 1, "")).ConfigureAwait(false);
            if (!await PlayItemAsync(c, head).ConfigureAwait(false))
            {
                state.Queue.PushFront(head);
            }

            return;
        }

        if (state.Queue.Add(item) is not { } position)
        {
            // Filled while the user was picking.
            await c.ReplyAsync($"🙅 The playlist is full ({MediaText.PlaylistMax} items).").ConfigureAwait(false);
            return;
        }

        if (more.Count == 0)
        {
            await c.ReplyAsync($"➕ Added to the playlist: **{MediaLabels.Format(item)}** (#{position})").ConfigureAwait(false);
            return;
        }

        await c.ReplyAsync(QueuedText([item, .. more], AppendAll(state, more) + 1, $"from #{position}")).ConfigureAwait(false);
    }

    /// <summary>Append <paramref name="items"/> in order while they fit; returns how many were appended.</summary>
    private static int AppendAll(GuildState state, IReadOnlyList<MediaItem> items)
    {
        var added = 0;
        foreach (var item in items)
        {
            if (state.Queue.Add(item) is null)
            {
                break;
            }

            added++;
        }

        return added;
    }

    /// <summary>
    /// <c>!playlist</c> (spec 021): on its own, the playlist and a menu of what to do with it; with <c>add</c>,
    /// <c>remove</c> or <c>order</c>, that action — asking for what it needs when it is not given.
    /// </summary>
    private async Task PlaylistAsync(CommandContext c)
    {
        var (word, rest) = CommandRouter.SplitFirst(c.Rest);
        switch (word.ToLowerInvariant())
        {
            case "":
                await ShowPlaylistAsync(c).ConfigureAwait(false);
                break;
            case "add":
                await PlaylistAddAsync(c.WithRest(rest)).ConfigureAwait(false);
                break;
            case "remove":
                await PlaylistRemoveAsync(c.WithRest(rest), listed: false).ConfigureAwait(false);
                break;
            case "order":
                await PlaylistOrderAsync(c.WithRest(rest), listed: false).ConfigureAwait(false);
                break;
            default:
                await c.ReplyAsync($"🙅 `!playlist` can `add`, `remove` or `order` — e.g. `!playlist add big buck bunny`. More: `!help playlist`.").ConfigureAwait(false);
                break;
        }
    }

    private Playlist<MediaItem> QueueOf(CommandContext c) =>
        ContextGuild(c) is { } g && _states.TryGetValue(g, out var state) ? state.Queue : EmptyQueue;

    /// <summary><c>!playlist add [title]</c>: queue it, or ask for a title first.</summary>
    private async Task PlaylistAddAsync(CommandContext c)
    {
        if (c.Rest.Length == 0)
        {
            await c.ReplyAsync(
                "➕ **What should we add?** Reply with a title — e.g. `big buck bunny`, an episode `example show s1e2`, "
                + "several `example show s1e1-6`, or just the show's name to pick from its episodes.").ConfigureAwait(false);
            if (await NumberPrompt.AskTextAsync(c).ConfigureAwait(false) is not { } title)
            {
                return;
            }

            c = c.WithRest(title);
        }

        await QueueAsync(c).ConfigureAwait(false);
    }

    /// <summary><c>!playlist remove [# or title]</c>: take it off, or ask which one first.</summary>
    private async Task PlaylistRemoveAsync(CommandContext c, bool listed)
    {
        var target = c.Rest;
        if (target.Length == 0)
        {
            var queue = QueueOf(c);
            if (queue.Count == 0)
            {
                await c.ReplyAsync("📭 The playlist is empty.").ConfigureAwait(false);
                return;
            }

            await c.ReplyAsync((listed ? "" : UpNextList(queue.Snapshot()) + "\n")
                + "➖ **Which one comes off?** Reply with its number or name.").ConfigureAwait(false);
            if (await NumberPrompt.AskTextAsync(c).ConfigureAwait(false) is not { } picked)
            {
                return;
            }

            target = picked;
        }

        // Looked up after the answer: the playlist may have been replaced meanwhile (left and rejoined).
        await c.ReplyAsync(QueueOf(c).Remove(target)).ConfigureAwait(false);
    }

    /// <summary><c>!playlist order [# or title] [new #]</c>: move it, or ask which one and where first.</summary>
    private async Task PlaylistOrderAsync(CommandContext c, bool listed)
    {
        var args = c.Rest;
        if (args.Length == 0)
        {
            var queue = QueueOf(c);
            if (queue.Count < 2)
            {
                await c.ReplyAsync(queue.Count == 0
                    ? "📭 The playlist is empty."
                    : "🙅 There's only one thing on the playlist — nothing to reorder.").ConfigureAwait(false);
                return;
            }

            await c.ReplyAsync((listed ? "" : UpNextList(queue.Snapshot()) + "\n")
                + "↕️ **Which one moves, and where to?** Reply with its number (or name), then the new position — "
                + "e.g. `3 1` moves item 3 to the top.").ConfigureAwait(false);
            if (await NumberPrompt.AskTextAsync(c).ConfigureAwait(false) is not { } picked)
            {
                return;
            }

            args = picked;
        }

        await c.ReplyAsync(QueueOf(c).Move(args)).ConfigureAwait(false);
    }

    private static string UpNextList(IReadOnlyList<MediaItem> queue) =>
        string.Join('\n', [$"📜 **Coming up** ({queue.Count}/{MediaText.PlaylistMax}):", .. queue.Select((it, i) => $"  `{i + 1}` {MediaLabels.Format(it)}")]);

    private static Playlist<MediaItem> EmptyQueue => new(MediaLabels.Format);

    private async Task AspectAsync(CommandContext c)
    {
        var guild = ContextGuild(c);
        if (guild is null)
        {
            await c.ReplyAsync("🙅 Join a voice channel first, then try again.").ConfigureAwait(false);
            return;
        }

        double? current = _aspect.TryGetValue(guild.Value, out var a) ? a : null;
        var choice = c.Rest.Trim();
        var named = choice.Length > 0 ? MediaText.FindAspect(choice) : null;
        if (named is null && choice.Length > 0 && choice.All(char.IsAsciiDigit))
        {
            await c.ReplyAsync($"🙅 Pick a number between 1 and {MediaText.AspectOptions.Count} — see `!ar`.").ConfigureAwait(false);
            return;
        }

        int index;
        if (named is { } found)
        {
            index = found;
        }
        else
        {
            var lines = new List<string>
            {
                (choice.Length > 0 ? $"🙅 `{Shorten(choice)}` isn't a picture shape. " : "") + "📐 **Aspect ratio** — reply with a number or name:",
            };
            for (var i = 0; i < MediaText.AspectOptions.Count; i++)
            {
                var option = MediaText.AspectOptions[i];
                var mark = Nullable.Equals(option.Ratio, current) ? "  ← current" : "";
                lines.Add($"`{i + 1}` {option.Label}{mark}");
            }

            await c.ReplyAsync(string.Join('\n', lines)).ConfigureAwait(false);
            if (await NumberPrompt.AskAsync(c, MediaText.AspectOptions.Count, text => MediaText.FindAspect(text) + 1).ConfigureAwait(false) is not { } picked)
            {
                return;
            }

            index = picked - 1;
        }

        var (label, ratio) = (MediaText.AspectOptions[index].Label, MediaText.AspectOptions[index].Ratio);
        if (ratio is { } r)
        {
            _aspect[guild.Value] = r;
        }
        else
        {
            _aspect.TryRemove(guild.Value, out _);
        }

        var name = label.Split(" — ")[0];
        var player = _engine.GetPlayback(guild.Value);
        _states.TryGetValue(guild.Value, out var state);
        if (player is not null && state?.Now is not null && player.IsSourceActive && !Nullable.Equals(player.Aspect, ratio))
        {
            player.SetAspect(ratio);
            await c.ReplyAsync($"📐 Aspect ratio set to **{name}** — adjusting the picture…").ConfigureAwait(false);
        }
        else
        {
            await c.ReplyAsync(player is not null && state?.Now is null
                ? $"📐 Aspect ratio set to **{name}** — from the next programme."
                : $"📐 Aspect ratio set to **{name}**.").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>!resolution</c> (<c>!res</c>, spec 010): the output height — <c>360p</c> … <c>1080p</c> or <c>default</c>
    /// (the configured one). Per guild, remembered; restarts the playing stream at its position.
    /// </summary>
    private async Task ResolutionAsync(CommandContext c)
    {
        var guild = ContextGuild(c);
        if (guild is null)
        {
            await c.ReplyAsync("🙅 Join a voice channel first, then try again.").ConfigureAwait(false);
            return;
        }

        int? current = _height.TryGetValue(guild.Value, out var h) ? h : null;
        var arg = c.Rest.Trim();
        var named = arg.Length > 0 ? MediaText.FindResolution(arg) : null;
        if (named is null && arg.Length > 0 && arg.All(char.IsAsciiDigit) && arg.Length <= 1)
        {
            await c.ReplyAsync($"🙅 Pick a number between 1 and {MediaText.ResolutionOptions.Count} — see `!res`.").ConfigureAwait(false);
            return;
        }

        int index;
        if (named is { } found)
        {
            index = found;
        }
        else
        {
            var lines = new List<string>
            {
                (arg.Length > 0 ? $"🙅 `{Shorten(arg)}` isn't a resolution. " : "") + "📺 **Resolution** — reply with a number or name:",
            };
            for (var i = 0; i < MediaText.ResolutionOptions.Count; i++)
            {
                var option = MediaText.ResolutionOptions[i];
                var mark = option.Height == current ? "  ← current" : "";
                lines.Add($"`{i + 1}` {option.Label}{mark}");
            }

            await c.ReplyAsync(string.Join('\n', lines)).ConfigureAwait(false);
            if (await NumberPrompt.AskAsync(c, MediaText.ResolutionOptions.Count, text => MediaText.FindResolution(text) + 1).ConfigureAwait(false) is not { } picked)
            {
                return;
            }

            index = picked - 1;
        }

        var choice = MediaText.ResolutionOptions[index];
        if (choice.Height is { } height)
        {
            _height[guild.Value] = height;
        }
        else
        {
            _height.TryRemove(guild.Value, out _);
        }

        var name = choice.Label.Split(" — ")[0];
        _states.TryGetValue(guild.Value, out var state);
        if (choice.Height == current || state?.Now is not { } now || _engine.GetPlayback(guild.Value) is not { IsSourceActive: true })
        {
            await c.ReplyAsync($"📺 Resolution set to **{name}**" + (choice.Height == current ? "." : " — from the next programme.")).ConfigureAwait(false);
            return;
        }

        await c.ReplyAsync($"📺 Resolution set to **{name}** — adjusting the picture…").ConfigureAwait(false);
        await RestartWithTracksAsync(guild.Value, state, now, now.Stream.Selection).ConfigureAwait(false);
    }

    /// <summary><c>!playlist</c> on its own: what's on and coming up, then a menu (add / remove / order) for a while.</summary>
    private async Task ShowPlaylistAsync(CommandContext c)
    {
        var guild = ContextGuild(c);
        GuildState? state = null;
        if (guild is { } g)
        {
            _states.TryGetValue(g, out state);
        }

        var player = guild is { } id ? _engine.GetPlayback(id) : null;
        var lines = new List<string>();
        if (state?.Now is { } now && player is not null)
        {
            var total = now.Item.RuntimeSeconds is { } rt ? $" / {MediaText.FormatTimestamp(rt)}" : "";
            var flag = player.IsPaused ? " (paused)" : "";
            lines.Add($"📺 **Now showing:** {MediaLabels.Format(now.Item)} — {MediaText.FormatTimestamp(player.Position)}{total}{flag}");
        }

        var queue = state?.Queue.Snapshot() ?? [];
        if (queue.Count > 0)
        {
            lines.Add(UpNextList(queue));
        }
        else
        {
            lines.Add(lines.Count == 0 ? "📭 Nothing on air and nothing on the playlist." : "_Nothing else on the playlist._");
        }

        // Spec 021: what can be done from here — remove needs an item, order needs two.
        var actions = new List<(string Word, string What)> { ("add", "add something to the end") };
        if (queue.Count > 0)
        {
            actions.Add(("remove", "take something off"));
        }

        if (queue.Count > 1)
        {
            actions.Add(("order", "move something up or down"));
        }

        lines.Add("");
        lines.Add("**Change it?** Reply with:");
        lines.AddRange(actions.Select(a => $"`{a.Word}` — {a.What}"));
        await c.ReplyAsync(string.Join('\n', lines)).ConfigureAwait(false);

        switch (await NumberPrompt.AskWordAsync(c, actions.Select(a => a.Word).ToList()).ConfigureAwait(false))
        {
            case "add":
                await PlaylistAddAsync(c.WithRest("")).ConfigureAwait(false);
                break;
            case "remove":
                await PlaylistRemoveAsync(c.WithRest(""), listed: true).ConfigureAwait(false);
                break;
            case "order":
                await PlaylistOrderAsync(c.WithRest(""), listed: true).ConfigureAwait(false);
                break;
        }
    }

    /// <summary><c>!media debug [on|off]</c> (.NET only, FR-050–FR-052).</summary>
    private async Task DebugAsync(CommandContext c)
    {
        var arg = c.Rest.Trim();
        var lower = arg.ToLowerInvariant();
        if (arg.Length > 0 && lower is not ("on" or "off"))
        {
            await c.ReplyAsync(CommandCatalog.Find("debug")!.HelpText()).ConfigureAwait(false);
            return;
        }

        var guild = ContextGuild(c);
        if (guild is null)
        {
            await c.ReplyAsync("🙅 Join a voice channel first, then try again.").ConfigureAwait(false);
            return;
        }

        var player = _engine.GetPlayback(guild.Value);
        if (arg.Length == 0)
        {
            var on = _debug.TryGetValue(guild.Value, out var d) && d;
            var text = $"🐞 Stats overlay is **{(on ? "on" : "off")}**";
            text += player is null ? " — nothing's on air." : $"\n```\n{player.DiagnosticsText()}\n```";
            await c.ReplyAsync(text).ConfigureAwait(false);
            return;
        }

        var enable = lower == "on";
        if (enable)
        {
            _debug[guild.Value] = true;
        }
        else
        {
            _debug.TryRemove(guild.Value, out _);
        }

        if (player is not null && player.IsSourceActive && player.DebugOverlay != enable)
        {
            player.SetDebugOverlay(enable);
            await c.ReplyAsync($"🐞 Stats overlay **{lower}**.").ConfigureAwait(false);
        }
        else
        {
            await c.ReplyAsync(player is null
                ? $"🐞 Stats overlay **{lower}** — from the next programme."
                : $"🐞 Stats overlay **{lower}**.").ConfigureAwait(false);
        }
    }

    // ── Resolution (search → pick) ───────────────────────────────────────────

    private async Task<bool> RequireLibraryAsync(CommandContext c)
    {
        if (_library is null)
        {
            await c.ReplyAsync(NotConfigured).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private async Task<MediaItem?> ResolveAsync(CommandContext c, PlayQuery query, QuietMiss? quiet = null, List<MediaItem>? more = null)
    {
        var library = _library!;
        var (show, season, episode) = (query.Title, query.Season, query.Episode);
        _log.LogInformation("Jellyfin search for {Show} by {Author} (season={Season} episode={Episode})", show, c.AuthorId, season, episode);

        // Spec 018: a range or list of episodes resolves through the show's seasons; the first is returned, the rest
        // go to `more` (played next / queued, like several picks from the episode list).
        var multi = query.Spans is not null && more is not null;
        IReadOnlyList<MediaItem> results;
        try
        {
            results = season is { } s && episode is { } e && !multi
                ? await library.FindEpisodeAsync(show, s, e, c.Cancellation).ConfigureAwait(false)
                : await library.SearchAsync(show, c.Cancellation).ConfigureAwait(false);
            if (season is not null && (episode is null || multi) && results.Any(r => r.Type == "Series"))
            {
                results = [.. results.Where(r => r.Type == "Series")]; // `<show> s3`: the shows, not same-named films
            }
            else if (multi)
            {
                results = []; // episodes asked for, but no show matched
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !c.Cancellation.IsCancellationRequested)
        {
            var unreachable = ex.InnerException is HttpRequestException or TaskCanceledException or TimeoutException
                || (ex is JellyfinException && ex.Message.Contains("HTTP", StringComparison.Ordinal));
            _log.LogError(ex, unreachable ? "could not reach Jellyfin" : "Jellyfin search failed");
            await c.ReplyAsync(unreachable
                ? "❌ We can't reach the Jellyfin library right now. Try again shortly — if it keeps happening, ask an admin."
                : "❌ The Jellyfin search didn't work. Try again — if it keeps happening, ask an admin to check the logs.").ConfigureAwait(false);
            return null;
        }

        if (results.Count == 0)
        {
            if (quiet is not null)
            {
                quiet.NothingFound = true;
                return null;
            }

            await c.ReplyAsync(NotFoundText(query)).ConfigureAwait(false);
            return null;
        }

        MediaItem item;
        if (results.Count == 1)
        {
            item = results[0];
        }
        else
        {
            var lines = new List<string> { $"🔎 Found {results.Count} matches for `{Shorten(query.Describe())}` — reply with a number:\n" };
            lines.AddRange(results.Select((it, i) => $"  `{i + 1}` [{it.Type ?? ""}] {MediaLabels.Format(it)}"));
            await c.ReplyAsync(string.Join('\n', lines)).ConfigureAwait(false);
            if (await NumberPrompt.AskAsync(c, results.Count).ConfigureAwait(false) is not { } choice)
            {
                return null;
            }

            item = results[choice - 1];
        }

        if (multi)
        {
            return await ResolveSpansAsync(c, item, query.Spans!, more!).ConfigureAwait(false);
        }

        return item.Type == "Series" ? await PickSeasonThenEpisodeAsync(c, item, episode is null ? season : null, more).ConfigureAwait(false) : item;
    }

    /// <summary>
    /// The episodes of <paramref name="series"/> that <paramref name="spans"/> name, in order (spec 018): returns the
    /// first and adds the rest to <paramref name="more"/>. Episodes or seasons that do not exist are reported and
    /// skipped; null (after a reply) when none exist.
    /// </summary>
    private async Task<MediaItem?> ResolveSpansAsync(CommandContext c, MediaItem series, IReadOnlyList<EpisodeSpan> spans, List<MediaItem> more)
    {
        var name = series.Name ?? "Unknown";
        IReadOnlyList<MediaItem> seasons;
        var episodesBySeason = new Dictionary<int, IReadOnlyList<MediaItem>>();
        try
        {
            seasons = await _library!.GetSeasonsAsync(series.Id, c.Cancellation).ConfigureAwait(false);
            foreach (var number in spans.SelectMany(sp => Enumerable.Range(sp.Season, sp.EndSeason - sp.Season + 1)).Distinct())
            {
                if (seasons.FirstOrDefault(s => s.IndexNumber == number) is { } seasonItem)
                {
                    episodesBySeason[number] = await _library.GetEpisodesAsync(series.Id, seasonItem.Id, c.Cancellation).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !c.Cancellation.IsCancellationRequested)
        {
            _log.LogError(ex, "failed to fetch episodes");
            await c.ReplyAsync("❌ We couldn't load the episodes from Jellyfin. Try again in a moment.").ConfigureAwait(false);
            return null;
        }

        var found = new List<MediaItem>();
        var missing = new List<(int Season, int? Episode)>();
        MediaItem? Episode(int season, int number) =>
            episodesBySeason.TryGetValue(season, out var eps) ? eps.FirstOrDefault(e => e.IndexNumber == number) : null;

        foreach (var span in spans)
        {
            if (span.Season == span.EndSeason)
            {
                if (!episodesBySeason.ContainsKey(span.Season))
                {
                    missing.Add((span.Season, null));
                    continue;
                }

                for (var n = span.Episode; n <= span.EndEpisode; n++)
                {
                    if (Episode(span.Season, n) is { } ep)
                    {
                        found.Add(ep);
                    }
                    else
                    {
                        missing.Add((span.Season, n));
                    }
                }

                continue;
            }

            // Across seasons: everything from the start episode to the end episode, in season order.
            foreach (var number in Enumerable.Range(span.Season, span.EndSeason - span.Season + 1))
            {
                if (!episodesBySeason.TryGetValue(number, out var eps))
                {
                    if (number == span.Season || number == span.EndSeason)
                    {
                        missing.Add((number, null));
                    }

                    continue;
                }

                found.AddRange(eps
                    .Where(e => e.IndexNumber is { } i && (number != span.Season || i >= span.Episode) && (number != span.EndSeason || i <= span.EndEpisode))
                    .OrderBy(e => e.IndexNumber));
            }

            foreach (var (s, e) in ((int, int)[])[(span.Season, span.Episode), (span.EndSeason, span.EndEpisode)])
            {
                if (episodesBySeason.ContainsKey(s) && Episode(s, e) is null)
                {
                    missing.Add((s, e));
                }
            }
        }

        found = [.. found.DistinctBy(e => e.Id)];
        if (found.Count == 0)
        {
            await c.ReplyAsync($"🔎 {string.Join(", ", spans)} of **{name}** isn't in the library.").ConfigureAwait(false);
            return null;
        }

        if (missing.Count > 0)
        {
            await c.ReplyAsync($"🔎 {MissingText(missing)} of **{name}** isn't in the library — carrying on with the rest.").ConfigureAwait(false);
        }

        more.AddRange(found.Skip(1));
        return found[0];
    }

    /// <summary>Missing episodes, consecutive ones joined: <c>S01E11–E12, S02E05, season 4</c>.</summary>
    internal static string MissingText(IReadOnlyList<(int Season, int? Episode)> missing)
    {
        var parts = new List<string>();
        foreach (var group in missing.Distinct().GroupBy(m => m.Season))
        {
            if (group.Any(m => m.Episode is null))
            {
                parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"season {group.Key}"));
                continue;
            }

            var numbers = group.Select(m => m.Episode!.Value).Order().ToList();
            for (var i = 0; i < numbers.Count;)
            {
                var j = i;
                while (j + 1 < numbers.Count && numbers[j + 1] == numbers[j] + 1)
                {
                    j++;
                }

                parts.Add(j == i
                    ? EpisodeSpan.Code(group.Key, numbers[i])
                    : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{EpisodeSpan.Code(group.Key, numbers[i])}–E{numbers[j]:00}"));
                i = j + 1;
            }
        }

        return string.Join(", ", parts);
    }

    /// <summary>Pick a season, then an episode; <paramref name="season"/> given (<c>&lt;show&gt; s3</c>) skips the season pick.</summary>
    private async Task<MediaItem?> PickSeasonThenEpisodeAsync(CommandContext c, MediaItem series, int? season = null, List<MediaItem>? more = null)
    {
        var name = series.Name ?? "Unknown";
        IReadOnlyList<MediaItem> seasons;
        try
        {
            seasons = await _library!.GetSeasonsAsync(series.Id, c.Cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !c.Cancellation.IsCancellationRequested)
        {
            _log.LogError(ex, "failed to fetch seasons");
            await c.ReplyAsync($"❌ We couldn't load the seasons of **{name}** from Jellyfin. Try again in a moment.").ConfigureAwait(false);
            return null;
        }

        if (seasons.Count == 0)
        {
            await c.ReplyAsync($"🔎 No seasons of **{name}** in the library.").ConfigureAwait(false);
            return null;
        }

        string? missing = null;
        if (season is { } wanted)
        {
            if (seasons.FirstOrDefault(s => s.IndexNumber == wanted) is { } chosen)
            {
                return await PickEpisodeAsync(c, series.Id, name, chosen, more).ConfigureAwait(false);
            }

            missing = $"🔎 **{name}** has no season {wanted}. ";
        }

        if (seasons.Count == 1 && missing is null)
        {
            return await PickEpisodeAsync(c, series.Id, name, seasons[0], more).ConfigureAwait(false);
        }

        var lines = new List<string> { $"{missing}📺 **{name}** — {seasons.Count} season{(seasons.Count == 1 ? "" : "s")}. Reply with a season:\n" };
        lines.AddRange(seasons.Select((s, i) => $"  `{i + 1}` {s.Name ?? $"Season {i + 1}"}"));
        await c.ReplyAsync(string.Join('\n', lines)).ConfigureAwait(false);
        if (await NumberPrompt.AskAsync(c, seasons.Count).ConfigureAwait(false) is not { } choice)
        {
            return null;
        }

        return await PickEpisodeAsync(c, series.Id, name, seasons[choice - 1], more).ConfigureAwait(false);
    }

    /// <summary>Pick an episode; with <paramref name="more"/>, several in order (<c>2,5,8</c>, <c>3-6</c>): the first is returned, the rest added to it.</summary>
    private async Task<MediaItem?> PickEpisodeAsync(CommandContext c, string seriesId, string seriesName, MediaItem season, List<MediaItem>? more = null)
    {
        IReadOnlyList<MediaItem> episodes;
        try
        {
            episodes = await _library!.GetEpisodesAsync(seriesId, season.Id, c.Cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !c.Cancellation.IsCancellationRequested)
        {
            _log.LogError(ex, "failed to fetch episodes");
            await c.ReplyAsync("❌ We couldn't load the episodes from Jellyfin. Try again in a moment.").ConfigureAwait(false);
            return null;
        }

        if (episodes.Count == 0)
        {
            await c.ReplyAsync($"🔎 No episodes of **{seriesName}** {season.Name} in the library.").ConfigureAwait(false);
            return null;
        }

        var pick = more is null || episodes.Count == 1 ? "Reply with an episode:" : "Reply with an episode, or several in order (`2,5,8`, `3-6`):";
        var lines = new List<string> { $"📺 **{seriesName} — {season.Name ?? "Unknown"}**. {pick}\n" };
        lines.AddRange(episodes.Select((ep, i) =>
            $"  `{i + 1}` {(ep.IndexNumber is { } n ? $"E{n:00} — {ep.Name ?? "Unknown"}" : ep.Name ?? "Unknown")}"));
        await c.ReplyAsync(string.Join('\n', lines)).ConfigureAwait(false);
        if (more is null || episodes.Count == 1)
        {
            return await NumberPrompt.AskAsync(c, episodes.Count).ConfigureAwait(false) is { } choice ? episodes[choice - 1] : null;
        }

        if (await NumberPrompt.AskManyAsync(c, episodes.Count).ConfigureAwait(false) is not { } picks)
        {
            return null;
        }

        more.AddRange(picks.Skip(1).Select(p => episodes[p - 1]));
        return episodes[picks[0] - 1];
    }

    // ── Playback ─────────────────────────────────────────────────────────────

    /// <summary>Play <paramref name="item"/> in the command author's voice channel.</summary>
    private async Task<bool> PlayItemAsync(CommandContext c, MediaItem item, double start = 0)
    {
        // Spec 023: the author's voice channel; from a server channel without one, the server's first voice channel.
        if (await _voice.ResolveTargetAsync(c.Message, c.Cancellation).ConfigureAwait(false) is not var (guild, channel, fallback))
        {
            await c.ReplyAsync("🙅 Join a voice channel first, then try again.").ConfigureAwait(false);
            return false;
        }

        if (fallback && _voiceModule is { } notice)
        {
            await c.ReplyAsync(notice.FallbackNotice).ConfigureAwait(false);
        }

        State(guild, c.ReplyAsync);
        var started = await StartItemAsync(guild, channel, item, start).ConfigureAwait(false);
        if (started)
        {
            StartPlaytimeLimit(guild);
            if (fallback && _voiceModule is { } voiceModule)
            {
                await voiceModule.WatchIfEmptyAsync(guild).ConfigureAwait(false); // nobody there yet: the countdown runs
            }
        }

        return started;
    }

    /// <summary>A user started playback: (re)start the <see cref="MaxPlaytime"/> countdown for the guild (spec 016).</summary>
    private void StartPlaytimeLimit(ulong guild)
    {
        if (MaxPlaytime <= TimeSpan.Zero)
        {
            return;
        }

        CancelPlaytimeLimit(guild);
        var cts = new CancellationTokenSource();
        PlaytimeLimits[guild] = (PlaytimeLimitAsync(guild, cts.Token), cts);
    }

    private void CancelPlaytimeLimit(ulong guild)
    {
        if (PlaytimeLimits.TryRemove(guild, out var running))
        {
            running.Cancel.Cancel();
        }
    }

    private async Task PlaytimeLimitAsync(ulong guild, CancellationToken ct)
    {
        try
        {
            await Task.Delay(MaxPlaytime, _time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_engine.IsStreaming(guild) || !_states.TryGetValue(guild, out var state))
        {
            return; // nothing is playing any more
        }

        var hours = MaxPlaytime.TotalHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        _log.LogInformation("stream in guild {Guild} reached the {Hours} h playtime limit — stopping", guild, hours);
        await StopStreamAsync(guild).ConfigureAwait(false);
        await state.Send($"⏹ That's {hours} hours on air — time for a break. `!play` to start again (the playlist is kept).").ConfigureAwait(false);
    }

    /// <summary>How a start ended.</summary>
    private enum StartResult
    {
        Started,

        /// <summary>The item can't play (no session, bad codec, go-live failed): try the next one.</summary>
        Failed,

        /// <summary>A stop/leave/teardown happened meanwhile: do nothing more (no advance, no retry).</summary>
        Abandoned,
    }

    /// <summary>
    /// Start a Jellyfin playback session for <paramref name="item"/> and stream it. Serialised per guild; the
    /// generation is bumped up front so a stream ending meanwhile can't auto-advance underneath this start.
    /// </summary>
    private async Task<bool> StartItemAsync(ulong guild, ulong channel, MediaItem item, double start = 0, bool sync = true)
    {
        var state = State(guild);
        await state.StartLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StartItemHoldingLockAsync(guild, state, channel, item, start, sync).ConfigureAwait(false) == StartResult.Started;
        }
        finally
        {
            state.StartLock.Release();
        }
    }

    /// <summary><see cref="StartItemAsync"/> body; the caller holds <c>state.StartLock</c>.</summary>
    private async Task<StartResult> StartItemHoldingLockAsync(
        ulong guild, GuildState state, ulong channel, MediaItem item, double start, bool sync, TrackSelection? tracks = null)
    {
        var generation = Interlocked.Increment(ref state.Generation);
        var hadPlayer = _engine.GetPlayback(guild) is not null;
        var result = await StartItemLockedAsync(guild, state, channel, item, start, sync, generation, tracks).ConfigureAwait(false);
        if (result == StartResult.Failed && Volatile.Read(ref state.Generation) == generation)
        {
            if (hadPlayer && _engine.GetPlayback(guild) is not null)
            {
                // Failed before replacing the current stream: let it keep auto-advancing when it ends.
                Interlocked.CompareExchange(ref state.Generation, generation - 1, generation);
            }
            else if (state.Now is { } stale && !_engine.IsStreaming(guild))
            {
                // The previous item ended while this start was failing; its end callback was suppressed by our
                // generation bump — release its transcode here so nothing is left holding it.
                state.Now = null;
                await _library!.StopEncodingAsync(stale.Stream.PlaySessionId, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <summary>The session is still the guild's current one and no newer start/stop happened.</summary>
    private bool StillCurrent(ulong guild, GuildState state, long generation) =>
        _states.TryGetValue(guild, out var current) && ReferenceEquals(current, state) && Volatile.Read(ref state.Generation) == generation;

    private async Task<StartResult> StartItemLockedAsync(
        ulong guild, GuildState state, ulong channel, MediaItem item, double start, bool sync, long generation, TrackSelection? tracks)
    {
        var library = _library!;
        var send = state.Send;
        var label = MediaLabels.Format(item);
        _log.LogInformation("Jellyfin: streaming {Label} (id: {Id}) from {Start:0}s", label, item.Id, start);

        await send($"📡 Tuning in to **{label}**…").ConfigureAwait(false);
        var allowDirect = !_directFailed.ContainsKey(item.Id);
        var stream = await library.StartPlaybackAsync(item.Id, tracks, CancellationToken.None, allowDirect).ConfigureAwait(false);
        if (stream is not null && tracks is null && ResolveTracks(guild, stream.Tracks) is { } preferred && preferred != stream.Selection)
        {
            // The remembered subtitle/audio language picks other tracks: reopen the session with them (spec 005).
            await library.StopEncodingAsync(stream.PlaySessionId, CancellationToken.None).ConfigureAwait(false);
            stream = await library.StartPlaybackAsync(item.Id, preferred, CancellationToken.None, allowDirect).ConfigureAwait(false);
        }

        if (stream is null)
        {
            await send($"❌ Jellyfin wouldn't play **{label}**. Ask an admin to check the server and the bot's Jellyfin login.").ConfigureAwait(false);
            return StillCurrent(guild, state, generation) ? StartResult.Failed : StartResult.Abandoned;
        }

        async Task<StartResult> Release(StartResult result)
        {
            await library.StopEncodingAsync(stream.PlaySessionId, CancellationToken.None).ConfigureAwait(false);
            return result == StartResult.Failed && !StillCurrent(guild, state, generation) ? StartResult.Abandoned : result;
        }

        Task<StartResult> Abandon()
        {
            // A stop/leave/teardown happened while this start was resolving: don't rejoin or stream.
            _log.LogInformation("Jellyfin: start of {Label} abandoned (stopped meanwhile)", label);
            return Release(StartResult.Abandoned);
        }

        _log.LogInformation(
            "Jellyfin: {Mode} URL: {Url}", stream.IsDirect ? "original file (direct stream)" : "transcoded stream", Redactor.RedactUrl(stream.Url));
        // A direct stream is the original file over HTTP: no HLS variants to resolve (spec 006).
        var url = stream.IsDirect ? stream.Url : await library.ResolveVariantAsync(stream.Url, CancellationToken.None).ConfigureAwait(false);

        string codec;
        bool hasAudio;
        var sourceFps = stream.FrameRate;
        if (stream.HasAudio is { } audio && stream.VideoCodec is { } known)
        {
            // PlaybackInfo already told us: skip ffprobe (seconds, and a second server-side transcode).
            (codec, hasAudio) = (known, audio);
        }
        else
        {
            var info = await library.ProbeAsync(url, CancellationToken.None).ConfigureAwait(false);
            if (info is null)
            {
                await send($"❌ Jellyfin couldn't prepare **{label}**. Try again — if it keeps happening, ask an admin to check the Jellyfin logs.").ConfigureAwait(false);
                return await Release(StartResult.Failed).ConfigureAwait(false);
            }

            (codec, hasAudio) = (info.Codec, info.HasAudio);
            sourceFps ??= info.Fps;
        }

        // The transcode path only gets codecs Jellyfin can hand over; a direct stream is decoded by the bot's FFmpeg,
        // which reads practically anything (av1, vp9, vc1, …).
        if (!stream.IsDirect && !SupportedCodecs.Contains(codec))
        {
            await send($"❌ **{label}** uses a video format (`{codec}`) we can't show.").ConfigureAwait(false);
            return await Release(StartResult.Failed).ConfigureAwait(false);
        }

        if (!StillCurrent(guild, state, generation))
        {
            return await Abandon().ConfigureAwait(false);
        }

        try
        {
            await _voice.EnsureAsync(guild, channel, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "could not join voice channel {Channel}", channel);
            await send("❌ We couldn't join your voice channel. Check the bot can see and join it, then try again.").ConfigureAwait(false);
            return await Release(StartResult.Failed).ConfigureAwait(false);
        }

        if (!StillCurrent(guild, state, generation))
        {
            return await Abandon().ConfigureAwait(false);
        }

        var previous = state.Now;
        state.Now = new NowPlaying(item, stream) { Reconnects = state.ResumeReconnects };
        state.ResumeReconnects = 0;
        var request = new StreamRequest
        {
            Title = label,
            Url = url,
            Live = true,
            HasAudio = hasAudio,
            // Direct stream: the bot picks the audio itself — the chosen track, else Jellyfin's default track (which is not
            // always the file's first audio stream, e.g. a commentary track first) — by its position among the file's
            // own audio tracks: Jellyfin's index also counts external files, so it is not the file's stream index.
            AudioTrack = stream.IsDirect ? JellyfinClient.PlayingAudio(stream.Tracks, stream.Selection.AudioIndex)?.EmbeddedOrdinal : null,
            ProbeSize = 10_000_000,
            StartOffset = start,
            Aspect = _aspect.TryGetValue(guild, out var aspect) ? aspect : null,
            OutputHeight = _height.TryGetValue(guild, out var height) ? height : null,
            DebugOverlay = _debug.TryGetValue(guild, out var debug) && debug,
            SourceCodec = codec,
            JellyfinTranscodeReason = stream.IsDirect ? null : stream.TranscodeReason ?? "Jellyfin chose it",
            JellyfinStats = stream.IsDirect ? null : ct => library.GetTranscodeStatsAsync(item.Id, ct),
            RuntimeSeconds = stream.RuntimeSeconds ?? item.RuntimeSeconds,
            SourceFps = sourceFps,
            TitleNote = !stream.IsDirect && _directFailed.ContainsKey(item.Id) ? "backup feed" : null,
        };

        _log.LogInformation(
            "starting {Label}: {Mode}, video {Codec}, audio {Audio}, source {Fps} fps, from {Start:0.0}s, tracks {Tracks}, height {Height}",
            label, stream.IsDirect ? "original file" : $"Jellyfin transcode ({stream.TranscodeReason ?? "Jellyfin chose it"})", codec,
            !hasAudio ? "none" : request.AudioTrack is { } ai ? $"file audio track {ai} (0:a:{ai})" : "default",
            request.SourceFps?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "?", start,
            stream.Selection, request.OutputHeight?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "configured");

        bool started;
        try
        {
            started = await _engine.StartAsync(guild, request, send, end => OnStreamEndAsync(guild, generation, end)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "go-live start failed for {Label}", label);
            started = false;
        }

        // The stream this start created (captured now, before anything else can register for the guild).
        var mine = started ? _engine.GetPlayback(guild) : null;

        if (previous is not null && !ReferenceEquals(previous.Stream, stream))
        {
            await library.StopEncodingAsync(previous.Stream.PlaySessionId, CancellationToken.None).ConfigureAwait(false);
        }

        if (!started)
        {
            if (Volatile.Read(ref state.Generation) == generation)
            {
                state.Now = null;
            }

            return await Release(StartResult.Failed).ConfigureAwait(false);
        }

        if (!StillCurrent(guild, state, generation))
        {
            // Stopped/torn down while go-live was connecting: end the stream we started — only that one.
            if (mine is not null)
            {
                await _engine.StopIfCurrentAsync(guild, mine, "abandoned").ConfigureAwait(false);
            }

            return await Abandon().ConfigureAwait(false);
        }

        if (sync && library.HasUserCredentials)
        {
            Spawn(SyncQueueAsync(guild, item.Id, start, mine));
        }

        return StartResult.Started;
    }

    /// <summary>
    /// A stream ended without a stop (naturally, the pipeline failed, or its connection dropped): resume after a
    /// dropped connection, else release the transcode and play the next item. Runs under the start lock so it can't
    /// race a user's start or skip; the generation check is atomic with taking <c>Now</c>.
    /// </summary>
    private async Task OnStreamEndAsync(ulong guild, long generation, PipelineEnd end)
    {
        if (!_states.TryGetValue(guild, out var state))
        {
            return;
        }

        await state.StartLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!StillCurrent(guild, state, generation))
            {
                return;
            }

            var finished = state.Now;
            var recovering = false;
            if (end.Reason == PipelineEndReason.Failed && !end.FirstFrameSent && finished is { Stream.IsDirect: true }
                && _voice.Get(guild) is { } retryLink
                && _directFailed.TryAdd(finished.Item.Id, 0))
            {
                // Spec 012: the original file never produced a frame (a format the bot's FFmpeg can't handle, a bad
                // seek, …): play the same item through Jellyfin's transcode, which decodes everything.
                _log.LogWarning(
                    "direct stream of {Item} failed before its first frame ({Error}); retrying through Jellyfin's transcode",
                    finished.Item.Id, end.Error);
                recovering = true;
                await state.Send("😅 Technical difficulties — that copy wouldn't play. Don't worry, this happens sometimes! Trying a backup feed now…").ConfigureAwait(false);
                await _library!.StopEncodingAsync(finished.Stream.PlaySessionId, CancellationToken.None).ConfigureAwait(false);
                StartResult retried;
                try
                {
                    retried = await StartItemHoldingLockAsync(
                        guild, state, retryLink.ChannelId, finished.Item, end.Position ?? 0, sync: false, tracks: finished.Stream.Selection).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "retrying {Item} through Jellyfin's transcode failed", finished.Item.Id);
                    await state.Send(RecoveryFailed).ConfigureAwait(false);
                    retried = StartResult.Failed;
                }

                if (retried != StartResult.Failed)
                {
                    return;
                }

                finished = state.Now;
            }

            if (end.Reason == PipelineEndReason.ConnectionLost && finished is not null && finished.Reconnects < MaxReconnects
                && _voice.Get(guild) is { } link
                && _voice.Gateway.GetVoiceChannel(guild, _voice.Gateway.SelfId) == link.ChannelId)
            {
                // The screenshare dropped (voice server restart/migration) while we are still in voice: resume the
                // same item where it was. Not after a kick (the bot is no longer in that channel).
                _log.LogInformation("resuming {Item} at {Position:0.0}s after the go-live connection dropped (attempt {Attempt})", finished.Item.Id, end.Position, finished.Reconnects + 1);
                recovering = true;
                await state.Send($"😅 Technical difficulties — the stream dropped. Don't worry, reconnecting ({finished.Reconnects + 1} of {MaxReconnects})…").ConfigureAwait(false);
                state.ResumeReconnects = finished.Reconnects + 1;
                StartResult resumed;
                try
                {
                    resumed = await StartItemHoldingLockAsync(guild, state, link.ChannelId, finished.Item, end.Position ?? 0, sync: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "resuming {Item} failed", finished.Item.Id);
                    await state.Send(RecoveryFailed).ConfigureAwait(false);
                    resumed = StartResult.Failed;
                }
                finally
                {
                    state.ResumeReconnects = 0;
                }

                if (resumed != StartResult.Failed)
                {
                    return; // resumed, or stopped meanwhile (then nothing more to do)
                }

                finished = state.Now; // a failed restart already released what it started
            }

            if (!recovering && end.Error is not null && end.Reason is PipelineEndReason.Failed or PipelineEndReason.ConnectionLost)
            {
                // Spec 020: no recovery was tried, so say plainly that it stopped (a failed recovery already said so).
                await state.Send(end.Reason != PipelineEndReason.ConnectionLost ? end.Error
                    : state.Queue.Count > 0 ? LostSignalNext
                    : StreamEngine.ConnectionLostMessage).ConfigureAwait(false);
            }

            state.Now = null;
            if (finished is not null)
            {
                await _library!.StopEncodingAsync(finished.Stream.PlaySessionId, CancellationToken.None).ConfigureAwait(false);
            }

            _log.LogInformation("stream ended ({Reason}) in guild {Guild}; advancing the playlist", end.Reason, guild);
            await AdvanceHoldingLockAsync(guild, state).ConfigureAwait(false);
        }
        finally
        {
            state.StartLock.Release();
        }
    }

    /// <summary>Start the next playable playlist item in the bot's voice channel (user skip).</summary>
    private async Task<bool> AdvanceAsync(ulong guild)
    {
        if (!_states.TryGetValue(guild, out var state))
        {
            return false;
        }

        await state.StartLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await AdvanceHoldingLockAsync(guild, state).ConfigureAwait(false);
        }
        finally
        {
            state.StartLock.Release();
        }
    }

    /// <summary>
    /// Try playlist items in order until one starts. Stops at once when a start is abandoned (a stop/leave happened):
    /// that item goes back to the head of the playlist, and nothing else is consumed.
    /// </summary>
    private async Task<bool> AdvanceHoldingLockAsync(ulong guild, GuildState state)
    {
        while (state.Queue.Count > 0)
        {
            if (_voice.Get(guild) is not { } link)
            {
                return false;
            }

            var item = state.Queue.TakeNext()!;
            await state.Send($"⏭ Coming up next: **{MediaLabels.Format(item)}**").ConfigureAwait(false);
            switch (await StartItemHoldingLockAsync(guild, state, link.ChannelId, item, 0, sync: true).ConfigureAwait(false))
            {
                case StartResult.Started:
                    return true;
                case StartResult.Abandoned:
                    if (_states.TryGetValue(guild, out var current) && ReferenceEquals(current, state))
                    {
                        state.Queue.PushFront(item); // a plain stop keeps the playlist intact
                    }

                    return false;
            }
        }

        return false;
    }

    /// <summary>Stop the go-live stream (voice stays) and release the transcode.</summary>
    private async Task StopStreamAsync(ulong guild)
    {
        if (_states.TryGetValue(guild, out var state))
        {
            Interlocked.Increment(ref state.Generation); // suppress auto-advance for the cancelled stream
            var finished = state.Now;
            state.Now = null;
            if (finished is not null && _library is not null)
            {
                await _library.StopEncodingAsync(finished.Stream.PlaySessionId, CancellationToken.None).ConfigureAwait(false);
            }
        }

        await _engine.StopAsync(guild).ConfigureAwait(false);
    }

    /// <summary>Forget the guild's session (playlist, SyncPlay group, transcode) and the debug overlay.</summary>
    private async Task TeardownAsync(ulong guild)
    {
        CancelPlaytimeLimit(guild);
        _debug.TryRemove(guild, out _);
        _trackPrefs.TryRemove(guild, out _);
        if (!_states.TryRemove(guild, out var state))
        {
            return;
        }

        Interlocked.Increment(ref state.Generation);
        if (state.Now is { } now && _library is not null)
        {
            await _library.StopEncodingAsync(now.Stream.PlaySessionId, CancellationToken.None).ConfigureAwait(false);
        }

        if (state.SyncPlay is { } group)
        {
            await SafeAsync(() => group.CloseAsync(CancellationToken.None), "closing SyncPlay").ConfigureAwait(false);
        }
    }

    // ── SyncPlay ─────────────────────────────────────────────────────────────

    private async Task<ISyncPlay?> EnsureSyncPlayAsync(ulong guild)
    {
        if (_library is not { HasUserCredentials: true } library)
        {
            return null;
        }

        var state = State(guild);
        if (state.SyncPlay is { Closed: false } existing)
        {
            return existing;
        }

        var group = library.CreateSyncPlay(guild, (cmd, _) => OnSyncCommandAsync(guild, cmd), (queue, _) => OnSyncQueueAsync(guild, queue));
        try
        {
            await group.OpenAsync(SyncPlayGroup.GroupNameFor(_guildName(guild) ?? guild.ToString(System.Globalization.CultureInfo.InvariantCulture)), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SyncPlay: could not create group");
            await SafeAsync(() => group.CloseAsync(CancellationToken.None), "closing SyncPlay").ConfigureAwait(false);
            return null;
        }

        state.SyncPlay = group;
        await state.Send($"👥 The Jellyfin SyncPlay group **{group.GroupName}** is open — join it from a Jellyfin app to control playback together.").ConfigureAwait(false);
        return group;
    }

    private async Task SyncQueueAsync(ulong guild, string itemId, double start, IPlayback? player)
    {
        if (player is not null)
        {
            // Start the group's clock from what Discord viewers actually see.
            var shown = await player.FirstFrameSent.WaitAsync(FirstFrameWait, _time).ContinueWith(t => t.IsCompletedSuccessfully, TaskScheduler.Default).ConfigureAwait(false);
            if (!ReferenceEquals(_engine.GetPlayback(guild), player))
            {
                return; // replaced or stopped meanwhile
            }

            if (shown)
            {
                start = player.Position;
            }
        }

        var state = State(guild);
        ISyncPlay? group;
        await state.SyncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            group = await EnsureSyncPlayAsync(guild).ConfigureAwait(false);
        }
        finally
        {
            state.SyncLock.Release();
        }

        if (group is null)
        {
            return;
        }

        state.SyncSent[itemId] = _time.GetTimestamp();
        await SafeAsync(() => group.SetQueueAsync(itemId, start, CancellationToken.None), "SyncPlay: set queue").ConfigureAwait(false);
    }

    /// <summary>Forward a local action to the guild's SyncPlay group, if any.</summary>
    private async Task SyncCallAsync(ulong guild, Func<ISyncPlay, CancellationToken, Task> action, string name)
    {
        if (!_states.TryGetValue(guild, out var state) || state.SyncPlay is not { Closed: false } group)
        {
            return;
        }

        state.SyncOwnCommandAt = _time.GetTimestamp();
        await SafeAsync(() => action(group, CancellationToken.None), $"SyncPlay: {name}").ConfigureAwait(false);
    }

    /// <summary>Apply a Pause/Unpause/Seek/Stop from another group member (our own echoes are no-ops).</summary>
    internal async Task OnSyncCommandAsync(ulong guild, SyncPlayCommand command)
    {
        var player = _engine.GetPlayback(guild);
        if (player is null || !_states.TryGetValue(guild, out var state))
        {
            return;
        }

        var position = command.PositionSeconds;
        var drift = position is { } p ? Math.Abs(p - player.Position) : 0.0;
        if (Elapsed(state.SyncOwnCommandAt) < SyncOwnCommandWindow)
        {
            drift = 0.0; // echo of our own request: trust our position
        }

        _log.LogInformation("SyncPlay {Command} at {Position} (local {Local:0.0}s)", command.Command, position?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "?", player.Position);
        var tolerance = SyncDriftTolerance.TotalSeconds;
        switch (command.Command)
        {
            case "Pause":
                if (!player.IsPaused)
                {
                    player.Pause();
                    await state.Send($"⏸ Paused from Jellyfin at {MediaText.FormatTimestamp(player.Position)}").ConfigureAwait(false);
                }

                break;

            case "Unpause":
                if (position is { } target && drift > tolerance)
                {
                    player.Seek(target);
                }

                if (player.IsPaused)
                {
                    player.Resume();
                    await state.Send("▶️ Resumed from Jellyfin.").ConfigureAwait(false);
                }

                break;

            case "Seek":
                if (position is { } to && drift > tolerance)
                {
                    player.Seek(to);
                    await state.Send($"⏩ Jellyfin jumped to {MediaText.FormatTimestamp(to)}").ConfigureAwait(false);
                }

                break;

            case "Stop":
                await StopStreamAsync(guild).ConfigureAwait(false);
                await state.Send("⏹ Stopped from Jellyfin.").ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Another member picked a different item in the group: play it.</summary>
    internal async Task OnSyncQueueAsync(ulong guild, SyncPlayQueue queue)
    {
        if (!_states.TryGetValue(guild, out var state) || queue.ItemId is not { Length: > 0 } itemId || _library is null)
        {
            return;
        }

        if (state.Now?.Item.Id == itemId)
        {
            return;
        }

        if (state.SyncSent.TryGetValue(itemId, out var sentAt) && Elapsed(sentAt) < SyncEchoWindow)
        {
            return; // late echo of our own queue change
        }

        if (_voice.Get(guild) is not { } link)
        {
            return;
        }

        if (await _library.GetItemAsync(itemId, CancellationToken.None).ConfigureAwait(false) is not { } item)
        {
            return;
        }

        await state.Send($"🔀 The Jellyfin group switched to **{MediaLabels.Format(item)}**").ConfigureAwait(false);
        await StartItemAsync(guild, link.ChannelId, item, queue.StartSeconds, sync: false).ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>The guild a command applies to: where the author is in voice, else the message's guild, else where the bot is.</summary>
    private ulong? ContextGuild(CommandContext c)
    {
        var (guild, _) = VoiceModule.ResolveAuthorVoice(_voice.Gateway, c.Message);
        return guild ?? c.GuildId ?? _voice.Any()?.GuildId;
    }

    private GuildState State(ulong guild, Func<string, Task>? send = null)
    {
        var state = _states.GetOrAdd(guild, _ => new GuildState());
        if (send is not null)
        {
            state.Send = send;
        }

        return state;
    }

    private TimeSpan Elapsed(long timestamp) => timestamp == long.MinValue ? TimeSpan.MaxValue : _time.GetElapsedTime(timestamp);

    private void Spawn(Task task)
    {
        Background[task] = 0;
        _ = task.ContinueWith(
            t =>
            {
                Background.TryRemove(t, out _);
                if (t.Exception is { } ex)
                {
                    _log.LogError(ex, "background media task failed");
                }
            },
            TaskScheduler.Default);
    }

    private async Task SafeAsync(Func<Task> action, string what)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{What} failed", what);
        }
    }

    // ── Subtitle and audio tracks (spec 005) ─────────────────────────────────

    /// <summary>Remembered per guild: subtitles on in a language (or off), audio in a language (null: default).</summary>
    private sealed record TrackPrefs(bool SubtitlesOn, string? SubtitleLanguage, string? AudioLanguage);

    /// <summary>The tracks the guild's remembered languages pick for an item (default/off when nothing matches).</summary>
    private TrackSelection ResolveTracks(ulong guild, IReadOnlyList<MediaTrack> tracks)
    {
        if (!_trackPrefs.TryGetValue(guild, out var prefs))
        {
            return TrackSelection.Default;
        }

        static bool Same(string? a, string? b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        int? audio = null;
        if (prefs.AudioLanguage is { } language && tracks.FirstOrDefault(t => t.IsAudio && Same(t.Language, language)) is { } a && !a.IsDefault)
        {
            audio = a.Index;
        }

        int? subtitle = null;
        if (prefs.SubtitlesOn && prefs.SubtitleLanguage is { } subLanguage)
        {
            var matches = tracks.Where(t => !t.IsAudio && Same(t.Language, subLanguage)).ToList();
            subtitle = (matches.FirstOrDefault(t => !t.IsForced) ?? matches.FirstOrDefault())?.Index;
        }

        return new TrackSelection(audio, subtitle);
    }

    /// <summary><c>!media subtitles</c> (<c>subs</c>, <c>sub</c>): picker, or <c>off</c> / a number; burned in by Jellyfin.</summary>
    private Task SubtitlesAsync(CommandContext c) => PickTrackAsync(c, audio: false);

    /// <summary><c>!media audio</c>: pick the audio track.</summary>
    private Task AudioTrackAsync(CommandContext c) => PickTrackAsync(c, audio: true);

    private async Task PickTrackAsync(CommandContext c, bool audio)
    {
        var arg = c.Rest.Trim();
        var guild = ContextGuild(c);
        if (guild is null || !_states.TryGetValue(guild.Value, out var state) || state.Now is not { } now || _engine.GetPlayback(guild.Value) is null)
        {
            await c.ReplyAsync(NothingOnAir).ConfigureAwait(false);
            return;
        }

        var tracks = now.Stream.Tracks.Where(t => t.IsAudio == audio).ToList();
        if (tracks.Count == 0 && (audio || !TrackMatch.IsOff(arg)))
        {
            await c.ReplyAsync(audio ? "🙅 There are no other audio tracks to choose from." : "🙅 This one has no subtitles.").ConfigureAwait(false);
            return;
        }

        // Options: subtitles get "Off" first.
        var options = new List<MediaTrack?>();
        if (!audio)
        {
            options.Add(null);
        }

        options.AddRange(tracks);
        var currentIndex = audio
            ? now.Stream.Selection.AudioIndex ?? tracks.FirstOrDefault(t => t.IsDefault)?.Index ?? tracks[0].Index
            : now.Stream.Selection.SubtitleIndex;

        var named = arg.Length > 0 ? TrackMatch.Find(options, arg, audio) : null;
        if (named is null && arg.Length > 0 && arg.All(char.IsAsciiDigit))
        {
            await c.ReplyAsync($"🙅 Pick a number between 1 and {options.Count} — see `!{(audio ? "audio" : "subs")}`.").ConfigureAwait(false);
            return;
        }

        int choice;
        if (named is { } found)
        {
            choice = found;
        }
        else
        {
            var title = audio ? "Audio track" : "Subtitles";
            var lines = new List<string> { (arg.Length > 0 ? $"🙅 No {(audio ? "audio track" : "subtitles")} match `{Shorten(arg)}`. " : "") + $"{(audio ? "🎧" : "💬")} **{title}** — reply with a number or language:" };
            for (var i = 0; i < options.Count; i++)
            {
                var label = options[i] is { } t ? TrackLabel(t) : "Off";
                var mark = options[i]?.Index == currentIndex ? "  ← current" : "";
                lines.Add($"`{i + 1}` {label}{mark}");
            }

            await c.ReplyAsync(string.Join('\n', lines)).ConfigureAwait(false);
            if (await NumberPrompt.AskAsync(c, options.Count, text => TrackMatch.Find(options, text, audio) + 1).ConfigureAwait(false) is not { } picked)
            {
                return;
            }

            choice = picked - 1;
        }

        var track = options[choice];
        var prefs = _trackPrefs.GetValueOrDefault(guild.Value, new TrackPrefs(false, null, null));
        _trackPrefs[guild.Value] = audio
            ? prefs with { AudioLanguage = track?.Language }
            : prefs with { SubtitlesOn = track is not null, SubtitleLanguage = track?.Language };

        var emoji = audio ? "🎧 Audio" : "💬 Subtitles";
        var name = track is null ? "off" : $"**{TrackLabel(track)}**";
        if (track?.Index == currentIndex)
        {
            await c.ReplyAsync($"{emoji}: {name} — already on.").ConfigureAwait(false);
            return;
        }

        var selection = audio
            ? now.Stream.Selection with { AudioIndex = track!.Index }
            : now.Stream.Selection with { SubtitleIndex = track?.Index };
        await c.ReplyAsync($"{emoji}: {name} — switching over…").ConfigureAwait(false);
        await RestartWithTracksAsync(guild.Value, state, now, selection).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>!language &lt;audio&gt; [subtitles]</c> (<c>!lang</c>, spec 019): the audio + subtitles shortcut — both set in one
    /// switch, e.g. <c>!language japanese english</c>; one word sets only the audio; <c>off</c> turns subtitles off. Bare: what
    /// is on now, and how to use it.
    /// </summary>
    private async Task LanguageAsync(CommandContext c)
    {
        var guild = ContextGuild(c);
        if (guild is null || !_states.TryGetValue(guild.Value, out var state) || state.Now is not { } now || _engine.GetPlayback(guild.Value) is null)
        {
            await c.ReplyAsync(c.Rest.Length == 0 ? CommandCatalog.Find("language")!.HelpText() : NothingOnAir).ConfigureAwait(false);
            return;
        }

        var audioTracks = now.Stream.Tracks.Where(t => t.IsAudio).ToList();
        var subtitleOptions = new List<MediaTrack?> { null };
        subtitleOptions.AddRange(now.Stream.Tracks.Where(t => !t.IsAudio));
        var playingAudio = JellyfinClient.PlayingAudio(now.Stream.Tracks, now.Stream.Selection.AudioIndex);
        var playingSubs = now.Stream.Tracks.FirstOrDefault(t => !t.IsAudio && t.Index == now.Stream.Selection.SubtitleIndex);
        var words = c.Rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            await c.ReplyAsync(
                $"🎧 Audio: **{(playingAudio is null ? "none" : TrackLabel(playingAudio))}** · 💬 Subtitles: **{(playingSubs is null ? "off" : TrackLabel(playingSubs))}**\n"
                + "-# `!language <audio> <subtitles>` sets both, e.g. `!language japanese english` · `!language english off`").ConfigureAwait(false);
            return;
        }

        if (words.Length > 2)
        {
            await c.ReplyAsync(CommandCatalog.Find("language")!.HelpText()).ConfigureAwait(false);
            return;
        }

        // Audio: one of the item's tracks by language / name / number.
        var audioPick = TrackMatch.Find([.. audioTracks], words[0], audio: true) is { } a ? audioTracks[a] : null;
        if (audioPick is null)
        {
            var choices = string.Join(", ", audioTracks.Select(t => TrackLabel(t)));
            await c.ReplyAsync($"🙅 No audio track matches `{Shorten(words[0])}` — this one has: {(choices.Length > 0 ? choices : "no audio")}.").ConfigureAwait(false);
            return;
        }

        // Subtitles: unchanged when not given; `off` or a language / name / number otherwise.
        var subtitlePick = playingSubs;
        if (words.Length == 2)
        {
            if (TrackMatch.Find(subtitleOptions, words[1], audio: false) is not { } s)
            {
                var choices = string.Join(", ", subtitleOptions.Skip(1).Select(t => TrackLabel(t!)));
                await c.ReplyAsync($"🙅 No subtitles match `{Shorten(words[1])}` — this one has: {(choices.Length > 0 ? choices : "none")} (or `off`).").ConfigureAwait(false);
                return;
            }

            subtitlePick = subtitleOptions[s];
        }

        var prefs = _trackPrefs.GetValueOrDefault(guild.Value, new TrackPrefs(false, null, null)) with { AudioLanguage = audioPick.Language };
        if (words.Length == 2)
        {
            prefs = prefs with { SubtitlesOn = subtitlePick is not null, SubtitleLanguage = subtitlePick?.Language };
        }

        _trackPrefs[guild.Value] = prefs;
        var summary = $"🎧 Audio: **{TrackLabel(audioPick)}** · 💬 Subtitles: **{(subtitlePick is null ? "off" : TrackLabel(subtitlePick))}**";
        if (audioPick.Index == playingAudio?.Index && subtitlePick?.Index == playingSubs?.Index)
        {
            await c.ReplyAsync($"{summary} — already on.").ConfigureAwait(false);
            return;
        }

        await c.ReplyAsync($"{summary} — switching over…").ConfigureAwait(false);
        await RestartWithTracksAsync(guild.Value, state, now, new TrackSelection(audioPick.Index, subtitlePick?.Index)).ConfigureAwait(false);
    }

    private static string TrackLabel(MediaTrack t) =>
        t.Label + (t.IsForced && !t.Label.Contains("forced", StringComparison.OrdinalIgnoreCase) ? " (forced)" : "");

    /// <summary>Reopen the current item (other tracks, or a new resolution) at its current position.</summary>
    private async Task RestartWithTracksAsync(ulong guild, GuildState state, NowPlaying now, TrackSelection selection)
    {
        await state.StartLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(state.Now, now) || _engine.GetPlayback(guild) is not { } playback || _voice.Get(guild) is not { } link)
            {
                return; // something else started or stopped meanwhile
            }

            await StartItemHoldingLockAsync(guild, state, link.ChannelId, now.Item, playback.Position, sync: false, tracks: selection).ConfigureAwait(false);
        }
        finally
        {
            state.StartLock.Release();
        }
    }

    private sealed record NowPlaying(MediaItem Item, PlaybackStream Stream)
    {
        /// <summary>How often this item was already resumed after a dropped connection.</summary>
        public int Reconnects { get; init; }
    }

    private sealed class GuildState
    {
        public long Generation;

        public Playlist<MediaItem> Queue { get; } = new(MediaLabels.Format);

        public volatile NowPlaying? Now;

        public Func<string, Task> Send { get; set; } = _ => Task.CompletedTask;

        public ISyncPlay? SyncPlay { get; set; }

        /// <summary>Item id → timestamp we last set it as the group's queue.</summary>
        public ConcurrentDictionary<string, long> SyncSent { get; } = new();

        public long SyncOwnCommandAt = long.MinValue;

        /// <summary>Reconnect count for the next start (set only when resuming after a dropped connection).</summary>
        public int ResumeReconnects;

        public SemaphoreSlim StartLock { get; } = new(1, 1);

        public SemaphoreSlim SyncLock { get; } = new(1, 1);
    }
}

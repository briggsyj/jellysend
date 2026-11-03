using System.Collections.Concurrent;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Slopsoil.Common.Logging;
using Slopsoil.Common.Permissions;
using Slopsoil.Commands.Interaction;
using Slopsoil.Commands.Routing;
using Slopsoil.Commands.Voice;
using Slopsoil.Services.Jellyfin;
using Slopsoil.Services.SyncPlay;
using Slopsoil.Streaming.Engine;

namespace Slopsoil.Commands.Media;

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

    private const string NotConfigured =
        "Jellyfin is not configured. Set `JELLYFIN_URL` and `JELLYFIN_API_KEY` (or `JELLYFIN_USERNAME` / "
        + "`JELLYFIN_PASSWORD`) in `.env` and restart the bot.";

    private readonly IMediaLibrary? _library;
    private readonly StreamEngine _engine;
    private readonly VoiceManager _voice;
    private readonly Func<ulong, string?> _guildName;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<ulong, GuildState> _states = new();

    // Preferences: aspect survives leaving voice; debug is cleared on leave.
    private readonly ConcurrentDictionary<ulong, double> _aspect = new();
    private readonly ConcurrentDictionary<ulong, bool> _debug = new();

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

    /// <summary>Background work (SyncPlay queue sync, auto-advance) — awaited by tests.</summary>
    internal ConcurrentDictionary<Task, byte> Background { get; } = new();

    public void Register(CommandRouter router)
    {
        router.Add("media", Role.Friend, MediaAsync)
            .Sub("search", Role.Friend, c => c.Rest.Trim().Length == 0 ? Task.CompletedTask : MediaAsync(c))
            .Sub("help", Role.Friend, HelpAsync)
            .Sub("pause", Role.Friend, PauseAsync)
            .Sub("play", Role.Friend, PlayAsync)
            .Sub("resume", Role.Friend, ResumeAsync)
            .Sub("stop", Role.Friend, StopCommandAsync)
            .Sub("skip", Role.Friend, SkipAsync)
            .Sub("queue", Role.Friend, QueueAsync)
            .Sub("order", Role.Friend, OrderAsync)
            .Sub("remove", Role.Friend, RemoveAsync)
            .Sub("aspect-ratio", Role.Friend, AspectAsync, "aspect", "ar")
            .Sub("playlist", Role.Friend, PlaylistAsync)
            .Sub("debug", Role.Friend, DebugAsync);
    }

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

    private async Task MediaAsync(CommandContext c) => await MediaQueryAsync(c, c.Rest).ConfigureAwait(false);

    private async Task MediaQueryAsync(CommandContext c, string query)
    {
        if (query.Trim().Length == 0)
        {
            await SendHelpAsync(c).ConfigureAwait(false);
            return;
        }

        if (!await RequireLibraryAsync(c).ConfigureAwait(false))
        {
            return;
        }

        var item = await ResolveAsync(c, query).ConfigureAwait(false);
        if (item is not null)
        {
            await PlayItemAsync(c, item).ConfigureAwait(false);
        }
    }

    /// <summary><c>!media stop making sense</c> is a title, not <c>stop</c> + junk: search it.</summary>
    private async Task<bool> TitleFallthroughAsync(CommandContext c, string word, string rest)
    {
        if (rest.Trim().Length == 0)
        {
            return false;
        }

        await MediaQueryAsync(c, $"{word} {rest.Trim()}").ConfigureAwait(false);
        return true;
    }

    private async Task HelpAsync(CommandContext c)
    {
        if (await TitleFallthroughAsync(c, "help", c.Rest).ConfigureAwait(false))
        {
            return;
        }

        await SendHelpAsync(c).ConfigureAwait(false);
    }

    private async Task SendHelpAsync(CommandContext c)
    {
        var text = MediaText.HelpText;
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
        if (await TitleFallthroughAsync(c, "pause", c.Rest).ConfigureAwait(false))
        {
            return;
        }

        var guild = ContextGuild(c);
        var player = guild is { } g ? _engine.GetPlayback(g) : null;
        if (guild is null || player is null)
        {
            await c.ReplyAsync("nothing is playing").ConfigureAwait(false);
            return;
        }

        if (player.IsPaused)
        {
            await c.ReplyAsync("already paused — `!media play` to carry on").ConfigureAwait(false);
            return;
        }

        player.Pause();
        await c.ReplyAsync($"⏸ paused at {MediaText.FormatTimestamp(player.Position)}").ConfigureAwait(false);
        await SyncCallAsync(guild.Value, (s, ct) => s.PauseAsync(ct), "pause").ConfigureAwait(false);
    }

    private async Task PlayAsync(CommandContext c)
    {
        if (await TitleFallthroughAsync(c, "play", c.Rest).ConfigureAwait(false))
        {
            return;
        }

        await PlayOrStartPlaylistAsync(c).ConfigureAwait(false);
    }

    private async Task PlayOrStartPlaylistAsync(CommandContext c)
    {
        var guild = ContextGuild(c);
        var player = guild is { } g ? _engine.GetPlayback(g) : null;
        if (guild is not null && player is not null)
        {
            if (!player.IsPaused)
            {
                await c.ReplyAsync("already playing").ConfigureAwait(false);
                return;
            }

            player.Resume();
            await c.ReplyAsync($"▶ resumed at {MediaText.FormatTimestamp(player.Position)}").ConfigureAwait(false);
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

        await c.ReplyAsync("nothing is paused — use `!media <title>` to play something").ConfigureAwait(false);
    }

    private async Task ResumeAsync(CommandContext c)
    {
        var text = c.Rest;
        if (text.Trim().Length == 0)
        {
            await PlayOrStartPlaylistAsync(c).ConfigureAwait(false);
            return;
        }

        var seconds = MediaText.ParseTimestamp(text);
        if (seconds is null && !text.Any(char.IsDigit))
        {
            // `!media resume the movie` — a title, not a time.
            await TitleFallthroughAsync(c, "resume", text).ConfigureAwait(false);
            return;
        }

        if (seconds is null)
        {
            await c.ReplyAsync($"`{text}` isn't a time — try `1:23:45`, `1.23.45`, `1:23` (h:mm), `45m` or `90s`").ConfigureAwait(false);
            return;
        }

        var guild = ContextGuild(c);
        var player = guild is { } g ? _engine.GetPlayback(g) : null;
        if (guild is null || player is null)
        {
            await c.ReplyAsync("nothing is playing").ConfigureAwait(false);
            return;
        }

        var runtime = _states.TryGetValue(guild.Value, out var state) ? state.Now?.Item.RuntimeSeconds : null;
        if (runtime is { } total && seconds >= total)
        {
            await c.ReplyAsync($"{MediaText.FormatTimestamp(seconds.Value)} is past the end ({MediaText.FormatTimestamp(total)})").ConfigureAwait(false);
            return;
        }

        var wasPaused = player.IsPaused;
        player.Seek(seconds.Value);
        player.Resume();
        await c.ReplyAsync($"⏩ jumping to {MediaText.FormatTimestamp(seconds.Value)}").ConfigureAwait(false);
        await SyncCallAsync(guild.Value, (s, ct) => s.SeekAsync(seconds.Value, ct), "seek").ConfigureAwait(false);
        if (wasPaused)
        {
            await SyncCallAsync(guild.Value, (s, ct) => s.UnpauseAsync(ct), "unpause").ConfigureAwait(false);
        }
    }

    private async Task StopCommandAsync(CommandContext c)
    {
        if (await TitleFallthroughAsync(c, "stop", c.Rest).ConfigureAwait(false))
        {
            return;
        }

        var guild = ContextGuild(c) ?? _voice.FindAny()?.GuildId;
        if (guild is null || (_voice.Find(guild.Value) is null && !_engine.IsStreaming(guild.Value)))
        {
            await c.ReplyAsync("not in a voice channel").ConfigureAwait(false);
            return;
        }

        await StopStreamAsync(guild.Value).ConfigureAwait(false);
        await TeardownAsync(guild.Value).ConfigureAwait(false);
        await _voice.LeaveAsync(guild.Value).ConfigureAwait(false);
        await c.ReplyAsync("⏹ stopped and left the voice channel").ConfigureAwait(false);
    }

    private async Task SkipAsync(CommandContext c)
    {
        if (await TitleFallthroughAsync(c, "skip", c.Rest).ConfigureAwait(false))
        {
            return;
        }

        var guild = ContextGuild(c);
        GuildState? state = null;
        if (guild is null || !_states.TryGetValue(guild.Value, out state) || (state.Queue.Count == 0 && state.Now is null))
        {
            await c.ReplyAsync("nothing to skip").ConfigureAwait(false);
            return;
        }

        if (state.Queue.Count == 0)
        {
            await StopStreamAsync(guild.Value).ConfigureAwait(false);
            await c.ReplyAsync("playlist is empty — stopped").ConfigureAwait(false);
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
        var query = c.Rest;
        if (query.Trim().Length == 0)
        {
            await c.ReplyAsync("usage: `!media queue <movie>` or `!media queue <show> sXXeYY`").ConfigureAwait(false);
            return;
        }

        if (!await RequireLibraryAsync(c).ConfigureAwait(false))
        {
            return;
        }

        var guild = ContextGuild(c);
        if (guild is null)
        {
            await c.ReplyAsync("you're not in a voice channel").ConfigureAwait(false);
            return;
        }

        var state = State(guild.Value, c.ReplyAsync);
        if (state.Queue.IsFull)
        {
            await c.ReplyAsync($"the playlist is full ({MediaText.PlaylistMax} items)").ConfigureAwait(false);
            return;
        }

        var item = await ResolveAsync(c, query).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        if (_engine.GetPlayback(guild.Value) is null && state.StartLock.CurrentCount > 0)
        {
            // Idle: play now — or, if a playlist is waiting, queue this and start the playlist from its head.
            if (state.Queue.Count == 0)
            {
                await PlayItemAsync(c, item).ConfigureAwait(false);
                return;
            }

            if (state.Queue.Add(item) is null)
            {
                await c.ReplyAsync($"the playlist is full ({MediaText.PlaylistMax} items)").ConfigureAwait(false);
                return;
            }

            var head = state.Queue.TakeNext()!;
            await c.ReplyAsync($"➕ queued **{MediaLabels.Format(item)}** at #{state.Queue.Count}").ConfigureAwait(false);
            if (!await PlayItemAsync(c, head).ConfigureAwait(false))
            {
                state.Queue.PushFront(head);
            }

            return;
        }

        if (state.Queue.Add(item) is not { } position)
        {
            // Filled while the user was picking.
            await c.ReplyAsync($"the playlist is full ({MediaText.PlaylistMax} items)").ConfigureAwait(false);
            return;
        }

        await c.ReplyAsync($"➕ queued **{MediaLabels.Format(item)}** at #{position}").ConfigureAwait(false);
    }

    private async Task OrderAsync(CommandContext c)
    {
        var guild = ContextGuild(c);
        var queue = guild is { } g && _states.TryGetValue(g, out var state) ? state.Queue : EmptyQueue;
        await c.ReplyAsync(queue.Move(c.Rest)).ConfigureAwait(false);
    }

    private async Task RemoveAsync(CommandContext c)
    {
        var guild = ContextGuild(c);
        var queue = guild is { } g && _states.TryGetValue(g, out var state) ? state.Queue : EmptyQueue;
        await c.ReplyAsync(queue.Remove(c.Rest)).ConfigureAwait(false);
    }

    private static Playlist<MediaItem> EmptyQueue => new(MediaLabels.Format);

    private async Task AspectAsync(CommandContext c)
    {
        var guild = ContextGuild(c);
        if (guild is null)
        {
            await c.ReplyAsync("you're not in a voice channel").ConfigureAwait(false);
            return;
        }

        double? current = _aspect.TryGetValue(guild.Value, out var a) ? a : null;
        var choice = c.Rest.Trim();
        int index;
        if (choice.Length > 0)
        {
            if (!choice.Any(char.IsDigit))
            {
                // `!media ar rahman` / `!media aspect of …` — a title, not a pick.
                var word = c.InvokedWith.Length > 0 ? c.InvokedWith : "aspect-ratio";
                if (await TitleFallthroughAsync(c, word, choice).ConfigureAwait(false))
                {
                    return;
                }
            }

            if (!int.TryParse(choice, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
                || n < 1 || n > MediaText.AspectOptions.Count)
            {
                await c.ReplyAsync($"pick a number between 1 and {MediaText.AspectOptions.Count} — see `!media aspect-ratio`").ConfigureAwait(false);
                return;
            }

            index = n - 1;
        }
        else
        {
            var lines = new List<string> { "**Aspect ratio** — reply with a number:" };
            for (var i = 0; i < MediaText.AspectOptions.Count; i++)
            {
                var option = MediaText.AspectOptions[i];
                var mark = Nullable.Equals(option.Ratio, current) ? "  ← current" : "";
                lines.Add($"`{i + 1}` {option.Label}{mark}");
            }

            await c.ReplyAsync(string.Join('\n', lines)).ConfigureAwait(false);
            if (await NumberPrompt.AskAsync(c, MediaText.AspectOptions.Count).ConfigureAwait(false) is not { } picked)
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
            await c.ReplyAsync($"📐 aspect ratio set to **{name}** — applying now").ConfigureAwait(false);
        }
        else
        {
            await c.ReplyAsync(player is not null && state?.Now is null
                ? $"📐 aspect ratio set to **{name}** — applies to the next Jellyfin item"
                : $"📐 aspect ratio set to **{name}**").ConfigureAwait(false);
        }
    }

    private async Task PlaylistAsync(CommandContext c)
    {
        if (await TitleFallthroughAsync(c, "playlist", c.Rest).ConfigureAwait(false))
        {
            return;
        }

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
            lines.Add($"**Now playing:** {MediaLabels.Format(now.Item)} — {MediaText.FormatTimestamp(player.Position)}{total}{flag}");
        }

        var queue = state?.Queue.Snapshot() ?? [];
        if (queue.Count > 0)
        {
            lines.Add($"**Up next** ({queue.Count}/{MediaText.PlaylistMax}):");
            lines.AddRange(queue.Select((it, i) => $"  `{i + 1}` {MediaLabels.Format(it)}"));
        }
        else if (lines.Count > 0)
        {
            lines.Add("_playlist is empty — add with `!media queue <title>`_");
        }

        await c.ReplyAsync(lines.Count == 0 ? "nothing playing and the playlist is empty" : string.Join('\n', lines)).ConfigureAwait(false);
    }

    /// <summary><c>!media debug [on|off]</c> (.NET only, FR-050–FR-052).</summary>
    private async Task DebugAsync(CommandContext c)
    {
        var arg = c.Rest.Trim();
        var lower = arg.ToLowerInvariant();
        if (arg.Length > 0 && lower is not ("on" or "off"))
        {
            await TitleFallthroughAsync(c, "debug", arg).ConfigureAwait(false);
            return;
        }

        var guild = ContextGuild(c);
        if (guild is null)
        {
            await c.ReplyAsync("you're not in a voice channel").ConfigureAwait(false);
            return;
        }

        var player = _engine.GetPlayback(guild.Value);
        if (arg.Length == 0)
        {
            var on = _debug.TryGetValue(guild.Value, out var d) && d;
            var text = $"🐞 debug overlay is **{(on ? "on" : "off")}**";
            text += player is null ? " — nothing is playing" : $"\n```\n{player.DiagnosticsText()}\n```";
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
            await c.ReplyAsync($"🐞 debug overlay **{lower}** — applying now").ConfigureAwait(false);
        }
        else
        {
            await c.ReplyAsync(player is null
                ? $"🐞 debug overlay **{lower}** — applies to the next item"
                : $"🐞 debug overlay **{lower}**").ConfigureAwait(false);
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

    private async Task<MediaItem?> ResolveAsync(CommandContext c, string query)
    {
        var library = _library!;
        var (show, season, episode) = MediaText.ParseEpisodeQuery(query);
        _log.LogInformation("Jellyfin search for {Show} by {Author} (season={Season} episode={Episode})", show, c.AuthorId, season, episode);

        IReadOnlyList<MediaItem> results;
        try
        {
            results = season is { } s && episode is { } e
                ? await library.FindEpisodeAsync(show, s, e, c.Cancellation).ConfigureAwait(false)
                : await library.SearchAsync(show, c.Cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !c.Cancellation.IsCancellationRequested)
        {
            var unreachable = ex.InnerException is HttpRequestException or TaskCanceledException or TimeoutException
                || (ex is JellyfinException && ex.Message.Contains("HTTP", StringComparison.Ordinal));
            _log.LogError(ex, unreachable ? "could not reach Jellyfin" : "Jellyfin search failed");
            await c.ReplyAsync(unreachable ? $"could not reach Jellyfin: {ex.Message}" : $"Jellyfin search failed: {ex.Message}").ConfigureAwait(false);
            return null;
        }

        if (results.Count == 0)
        {
            await c.ReplyAsync(season is { } ns && episode is { } ne
                ? $"S{ns:00}E{ne:00} of `{show}` not found in Jellyfin"
                : $"nothing found in Jellyfin for `{query}`").ConfigureAwait(false);
            return null;
        }

        MediaItem item;
        if (results.Count == 1)
        {
            item = results[0];
        }
        else
        {
            var lines = new List<string> { $"Found {results.Count} result(s) for `{query}`. Pick a number:\n" };
            lines.AddRange(results.Select((it, i) => $"  `{i + 1}` [{it.Type ?? ""}] {MediaLabels.Format(it)}"));
            await c.ReplyAsync(string.Join('\n', lines)).ConfigureAwait(false);
            if (await NumberPrompt.AskAsync(c, results.Count).ConfigureAwait(false) is not { } choice)
            {
                return null;
            }

            item = results[choice - 1];
        }

        return item.Type == "Series" ? await PickSeasonThenEpisodeAsync(c, item).ConfigureAwait(false) : item;
    }

    private async Task<MediaItem?> PickSeasonThenEpisodeAsync(CommandContext c, MediaItem series)
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
            await c.ReplyAsync($"failed to fetch seasons: {ex.Message}").ConfigureAwait(false);
            return null;
        }

        if (seasons.Count == 0)
        {
            await c.ReplyAsync($"no seasons found for **{name}**").ConfigureAwait(false);
            return null;
        }

        if (seasons.Count == 1)
        {
            return await PickEpisodeAsync(c, series.Id, name, seasons[0]).ConfigureAwait(false);
        }

        var lines = new List<string> { $"**{name}** — {seasons.Count} seasons. Pick a season:\n" };
        lines.AddRange(seasons.Select((s, i) => $"  `{i + 1}` {s.Name ?? $"Season {i + 1}"}"));
        await c.ReplyAsync(string.Join('\n', lines)).ConfigureAwait(false);
        if (await NumberPrompt.AskAsync(c, seasons.Count).ConfigureAwait(false) is not { } choice)
        {
            return null;
        }

        return await PickEpisodeAsync(c, series.Id, name, seasons[choice - 1]).ConfigureAwait(false);
    }

    private async Task<MediaItem?> PickEpisodeAsync(CommandContext c, string seriesId, string seriesName, MediaItem season)
    {
        IReadOnlyList<MediaItem> episodes;
        try
        {
            episodes = await _library!.GetEpisodesAsync(seriesId, season.Id, c.Cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !c.Cancellation.IsCancellationRequested)
        {
            _log.LogError(ex, "failed to fetch episodes");
            await c.ReplyAsync($"failed to fetch episodes: {ex.Message}").ConfigureAwait(false);
            return null;
        }

        if (episodes.Count == 0)
        {
            await c.ReplyAsync($"no episodes found for **{season.Name}** of **{seriesName}**").ConfigureAwait(false);
            return null;
        }

        var lines = new List<string> { $"**{seriesName} — {season.Name ?? "Unknown"}**. Pick an episode:\n" };
        lines.AddRange(episodes.Select((ep, i) =>
            $"  `{i + 1}` {(ep.IndexNumber is { } n ? $"E{n:00} — {ep.Name ?? "Unknown"}" : ep.Name ?? "Unknown")}"));
        await c.ReplyAsync(string.Join('\n', lines)).ConfigureAwait(false);
        return await NumberPrompt.AskAsync(c, episodes.Count).ConfigureAwait(false) is { } choice ? episodes[choice - 1] : null;
    }

    // ── Playback ─────────────────────────────────────────────────────────────

    /// <summary>Play <paramref name="item"/> in the command author's voice channel.</summary>
    private async Task<bool> PlayItemAsync(CommandContext c, MediaItem item, double start = 0)
    {
        var (guild, channel) = VoiceModule.ResolveAuthorVoice(_voice.Gateway, c.Message);
        if (guild is null || channel is null)
        {
            await c.ReplyAsync("you're not in a voice channel").ConfigureAwait(false);
            return false;
        }

        State(guild.Value, c.ReplyAsync);
        return await StartItemAsync(guild.Value, channel.Value, item, start).ConfigureAwait(false);
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
    private async Task<StartResult> StartItemHoldingLockAsync(ulong guild, GuildState state, ulong channel, MediaItem item, double start, bool sync)
    {
        var generation = Interlocked.Increment(ref state.Generation);
        var hadPlayer = _engine.GetPlayback(guild) is not null;
        var result = await StartItemLockedAsync(guild, state, channel, item, start, sync, generation).ConfigureAwait(false);
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

    private async Task<StartResult> StartItemLockedAsync(ulong guild, GuildState state, ulong channel, MediaItem item, double start, bool sync, long generation)
    {
        var library = _library!;
        var send = state.Send;
        var label = MediaLabels.Format(item);
        _log.LogInformation("Jellyfin: streaming {Label} (id: {Id}) from {Start:0}s", label, item.Id, start);

        await send("checking stream…").ConfigureAwait(false);
        var stream = await library.StartPlaybackAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
        if (stream is null)
        {
            await send($"could not start a Jellyfin playback session for **{label}** — check that the server is reachable and the credentials are valid").ConfigureAwait(false);
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

        _log.LogInformation("Jellyfin: stream URL: {Url}", Redactor.RedactUrl(stream.Url));
        var url = await library.ResolveVariantAsync(stream.Url, CancellationToken.None).ConfigureAwait(false);

        string codec;
        bool hasAudio;
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
                await send($"could not probe the Jellyfin stream for **{label}** — transcoding may have failed on the server").ConfigureAwait(false);
                return await Release(StartResult.Failed).ConfigureAwait(false);
            }

            (codec, hasAudio) = (info.Codec, info.HasAudio);
        }

        if (!SupportedCodecs.Contains(codec))
        {
            await send($"unsupported video codec `{codec}` from Jellyfin for **{label}**").ConfigureAwait(false);
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
            await send($"could not join the voice channel: {ex.Message}").ConfigureAwait(false);
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
            ProbeSize = 10_000_000,
            StartOffset = start,
            Aspect = _aspect.TryGetValue(guild, out var aspect) ? aspect : null,
            DebugOverlay = _debug.TryGetValue(guild, out var debug) && debug,
            SourceCodec = codec,
            RuntimeSeconds = stream.RuntimeSeconds ?? item.RuntimeSeconds,
        };

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
            if (end.Reason == PipelineEndReason.ConnectionLost && finished is not null && finished.Reconnects < MaxReconnects
                && _voice.Get(guild) is { } link
                && _voice.Gateway.GetVoiceChannel(guild, _voice.Gateway.SelfId) == link.ChannelId)
            {
                // The screenshare dropped (voice server restart/migration) while we are still in voice: resume the
                // same item where it was. Not after a kick (the bot is no longer in that channel).
                _log.LogInformation("resuming {Item} at {Position:0.0}s after the go-live connection dropped (attempt {Attempt})", finished.Item.Id, end.Position, finished.Reconnects + 1);
                await state.Send($"↻ reconnecting the screenshare (attempt {finished.Reconnects + 1} of {MaxReconnects})…").ConfigureAwait(false);
                state.ResumeReconnects = finished.Reconnects + 1;
                StartResult resumed;
                try
                {
                    resumed = await StartItemHoldingLockAsync(guild, state, link.ChannelId, finished.Item, end.Position ?? 0, sync: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "resuming {Item} failed", finished.Item.Id);
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
            await state.Send($"⏭ up next: **{MediaLabels.Format(item)}**").ConfigureAwait(false);
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
        _debug.TryRemove(guild, out _);
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
        await state.Send($"👥 Jellyfin SyncPlay group **{group.GroupName}** is open — join it from a Jellyfin app to control playback together").ConfigureAwait(false);
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
                    await state.Send($"⏸ paused from Jellyfin at {MediaText.FormatTimestamp(player.Position)}").ConfigureAwait(false);
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
                    await state.Send("▶ resumed from Jellyfin").ConfigureAwait(false);
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
                await state.Send("⏹ stopped from Jellyfin").ConfigureAwait(false);
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

        await state.Send($"🔀 Jellyfin group switched to **{MediaLabels.Format(item)}**").ConfigureAwait(false);
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

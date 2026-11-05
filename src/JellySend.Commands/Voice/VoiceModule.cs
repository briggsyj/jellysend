using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using JellySend.Common.Permissions;
using JellySend.Commands.Routing;

namespace JellySend.Commands.Voice;

/// <summary><c>!join</c>, <c>!leave</c>, <c>!stop</c> and auto-leave (<c>cogs/voice.py</c>).</summary>
public sealed class VoiceModule(
    VoiceManager voice, IStreamControl streams, ILogger<VoiceModule>? logger = null, TimeProvider? time = null, TimeSpan? emptyChannelTimeout = null)
{
    /// <summary>Spec 003: how long the bot stays in a channel nobody else is in (zero = leave at once).</summary>
    public TimeSpan EmptyChannelTimeout { get; } = emptyChannelTimeout ?? TimeSpan.Zero;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, CancellationTokenSource> _emptyTimers = new();

    /// <summary>The empty-channel countdown task per guild (tests await it).</summary>
    internal System.Collections.Concurrent.ConcurrentDictionary<ulong, Task> EmptyCountdowns { get; } = new();

    public const string StoppedReply = "⏹ Off air — still in the channel. `!leave` to sign off.";

    public const string LeftReply = "📴 Signing off — left the voice channel.";

    /// <summary>How long a self "left voice" waits before tearing down: a move (leave + join) rejoins within it.</summary>
    public static readonly TimeSpan SelfLeftGrace = TimeSpan.FromSeconds(2);

    private readonly ILogger _log = logger ?? NullLogger<VoiceModule>.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;


    /// <summary><c>resolve_voice</c>: (guild, author's voice channel) for guild and DM messages.</summary>
    public static (ulong? Guild, ulong? Channel) ResolveAuthorVoice(IVoiceGateway gateway, ChatMessage message)
    {
        if (message.GuildId is { } guild)
        {
            return (guild, gateway.GetVoiceChannel(guild, message.AuthorId));
        }

        return gateway.FindUserVoice(message.AuthorId) is { } found ? (found.Guild, found.Channel) : (null, null);
    }

    /// <summary>
    /// <c>!media join</c> / <c>!join</c>: join (or move to) the author's voice channel; from a server channel when the
    /// author isn't in voice, the server's first voice channel (spec 023).
    /// </summary>
    public async Task JoinAsync(CommandContext c)
    {
        if (await voice.ResolveTargetAsync(c.Message, c.Cancellation) is not var (guild, channel, fallback))
        {
            await c.ReplyAsync("🙅 Join a voice channel first, then try again.");
            return;
        }

        if (fallback)
        {
            await c.ReplyAsync(FallbackNotice);
        }

        await voice.EnsureAsync(guild, channel, c.Cancellation);
        await c.ReplyAsync("📡 Joined — ready to broadcast.");
        if (fallback)
        {
            await WatchIfEmptyAsync(guild);
        }
    }

    /// <summary>Said when the bot broadcasts in the server's first voice channel because the author isn't in voice.</summary>
    public string FallbackNotice => EmptyChannelTimeout > TimeSpan.Zero
        ? $"📡 You're not in a voice channel, so we're broadcasting in this server's first one — come and join! "
          + $"We'll sign off if nobody joins within {Minutes(EmptyChannelTimeout)}."
        : "📡 You're not in a voice channel, so we're broadcasting in this server's first one — come and join!";

    private static string Minutes(TimeSpan t) =>
        t.TotalMinutes == 1 ? "1 minute" : $"{t.TotalMinutes.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} minutes";

    /// <summary>
    /// Spec 023: after joining a channel nobody else is in, start the empty-channel countdown (the bot's own join does
    /// not start it). Not with a timeout of 0, which would stop what was just started.
    /// </summary>
    public async Task WatchIfEmptyAsync(ulong guildId)
    {
        if (EmptyChannelTimeout > TimeSpan.Zero && BotChannel(guildId) is { } channel && !HasOthers(guildId, channel))
        {
            await OnChannelEmptyAsync(guildId, channel).ConfigureAwait(false);
        }
    }

    /// <summary><c>!media leave</c> / <c>!leave</c>: stop the stream, discard the session and leave voice.</summary>
    public async Task LeaveAsync(CommandContext c)
    {
        // Accept a stale link (kicked / dropped) too, so the stream and session can still be cleaned up.
        var link = c.GuildId is { } g ? voice.Find(g) : voice.FindAny();
        var guild = link?.GuildId ?? c.GuildId;
        if (guild is null || (link is null && !streams.IsStreaming(guild.Value)))
        {
            await c.ReplyAsync("🙅 I'm not in a voice channel.");
            return;
        }

        _log.LogInformation("disconnecting from voice in guild {Guild}", guild);
        await streams.StopAsync(guild.Value, "leave");
        await voice.LeaveAsync(guild.Value);
        await c.ReplyAsync(LeftReply);
    }

    /// <summary><c>!media stop</c> / <c>!stop</c>: stop the stream, stay in voice, keep the playlist.</summary>
    public async Task StopAsync(CommandContext c)
    {
        var (guild, _) = ResolveAuthorVoice(voice.Gateway, c.Message);
        guild ??= c.GuildId ?? voice.FindAny()?.GuildId;
        if (guild is null || !streams.IsStreaming(guild.Value))
        {
            await c.ReplyAsync("🙅 Nothing's on air right now.");
            return;
        }

        _log.LogInformation("stopping stream in guild {Guild}", guild);
        await streams.StopAsync(guild.Value, "stop");
        await c.ReplyAsync(StoppedReply);
    }

    /// <summary>
    /// Call when the voice-state cache saw <paramref name="userId"/> move from <paramref name="before"/> to
    /// <paramref name="after"/> (<see cref="JellySend.Commands.Voice.IVoiceGateway"/> cache already updated).
    /// Someone else leaving our channel → auto-leave when nobody but the bot remains; the bot itself leaving
    /// voice (kicked, disconnected) → tear the session down.
    /// </summary>
    public async Task OnVoiceStateChangedAsync(ulong guildId, ulong userId, ulong? before, ulong? after)
    {
        var self = voice.Gateway.SelfId;
        if (userId == self)
        {
            if (after is null)
            {
                CancelCountdown(guildId, "the bot left voice");
                await OnSelfLeftAsync(guildId).ConfigureAwait(false);
            }
            else if (voice.Find(guildId) is not null && !HasOthers(guildId, after.Value))
            {
                await OnChannelEmptyAsync(guildId, after.Value).ConfigureAwait(false); // moved into an empty channel
            }

            return;
        }

        if (voice.Get(guildId) is null || BotChannel(guildId) is not { } channel)
        {
            return;
        }

        if (after == channel && before != channel)
        {
            CancelCountdown(guildId, $"user {userId} joined"); // someone (re)joined: keep streaming
            return;
        }

        if (before != channel || after == channel || HasOthers(guildId, channel))
        {
            return; // only the last person leaving the bot's own channel matters
        }

        await OnChannelEmptyAsync(guildId, channel).ConfigureAwait(false);
    }

    /// <summary>The bot's current voice channel: the voice-state cache (follows moderator moves), else its link.</summary>
    private ulong? BotChannel(ulong guildId) =>
        voice.Gateway.GetVoiceChannel(guildId, voice.Gateway.SelfId) ?? voice.Get(guildId)?.ChannelId;

    private bool HasOthers(ulong guildId, ulong channelId)
    {
        var self = voice.Gateway.SelfId;
        return voice.Gateway.UsersInChannel(guildId, channelId).Any(u => u != self);
    }

    /// <summary>Nobody but the bot is left in its channel: leave now (timeout 0) or start the countdown.</summary>
    private async Task OnChannelEmptyAsync(ulong guildId, ulong channelId)
    {
        if (EmptyChannelTimeout <= TimeSpan.Zero)
        {
            _log.LogInformation("all users left channel {Channel} in guild {Guild} — disconnecting", channelId, guildId);
            await streams.StopAsync(guildId, "auto-leave");
            await voice.LeaveAsync(guildId);
            return;
        }

        var cts = new CancellationTokenSource();
        if (_emptyTimers.TryGetValue(guildId, out var running) && !running.IsCancellationRequested)
        {
            cts.Dispose();
            return; // already counting down since the channel first became empty
        }

        _emptyTimers[guildId] = cts;
        _log.LogInformation(
            "channel {Channel} in guild {Guild} is empty — stopping in {Minutes} min unless someone comes back",
            channelId, guildId, EmptyChannelTimeout.TotalMinutes);
        EmptyCountdowns[guildId] = CountdownAsync(guildId, cts);
    }

    private async Task CountdownAsync(ulong guildId, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(EmptyChannelTimeout, _time, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _emptyTimers.TryRemove(new KeyValuePair<ulong, CancellationTokenSource>(guildId, cts));
        // Re-check at expiry: still in voice, and still nobody else in the bot's channel.
        if (voice.Find(guildId) is null || BotChannel(guildId) is not { } channel || HasOthers(guildId, channel))
        {
            return;
        }

        _log.LogInformation("channel {Channel} in guild {Guild} stayed empty for {Minutes} min — stopping and leaving", channel, guildId, EmptyChannelTimeout.TotalMinutes);
        await streams.StopAsync(guildId, "empty-timeout");
        await voice.LeaveAsync(guildId);
    }

    private void CancelCountdown(ulong guildId, string why)
    {
        if (_emptyTimers.TryRemove(guildId, out var cts))
        {
            _log.LogInformation("empty-channel countdown in guild {Guild} cancelled: {Why}", guildId, why);
            cts.Cancel();
        }
    }

    /// <summary>Longest a self "left voice" check waits for a start/join in progress before deciding.</summary>
    public static readonly TimeSpan SelfLeftBusyLimit = TimeSpan.FromSeconds(60);

    /// <summary>The bot left voice without !leave (kicked, moved out, connection lost): discard the session.</summary>
    private async Task OnSelfLeftAsync(ulong guildId)
    {
        await Task.Delay(SelfLeftGrace, _time).ConfigureAwait(false);

        // A start or move in progress may legitimately leave and rejoin: wait for it to finish, then decide on the
        // state it left behind (giving up here would leave a kicked session alive forever).
        var waited = TimeSpan.Zero;
        while ((voice.IsBusy(guildId) || streams.IsStarting(guildId)) && waited < SelfLeftBusyLimit)
        {
            await Task.Delay(SelfLeftGrace, _time).ConfigureAwait(false);
            waited += SelfLeftGrace;
        }

        if (voice.Gateway.GetVoiceChannel(guildId, voice.Gateway.SelfId) is not null)
        {
            return; // moved/rejoined
        }

        if (voice.Find(guildId) is null && !streams.IsStreaming(guildId))
        {
            return; // already cleaned up (our own !leave)
        }

        _log.LogInformation("left voice in guild {Guild} without !leave — stopping and discarding the session", guildId);
        await streams.StopAsync(guildId, "disconnected");
        await voice.LeaveAsync(guildId);
    }
}

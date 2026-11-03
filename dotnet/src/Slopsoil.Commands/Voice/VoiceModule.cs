using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Slopsoil.Common.Permissions;
using Slopsoil.Commands.Routing;

namespace Slopsoil.Commands.Voice;

/// <summary><c>!join</c>, <c>!leave</c>, <c>!stop</c> and auto-leave (<c>cogs/voice.py</c>).</summary>
public sealed class VoiceModule(VoiceManager voice, IStreamControl streams, ILogger<VoiceModule>? logger = null, TimeProvider? time = null)
{
    /// <summary>How long a self "left voice" waits before tearing down: a move (leave + join) rejoins within it.</summary>
    public static readonly TimeSpan SelfLeftGrace = TimeSpan.FromSeconds(2);

    private readonly ILogger _log = logger ?? NullLogger<VoiceModule>.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public void Register(CommandRouter router)
    {
        router.Add("join", Role.Friend, JoinAsync);
        router.Add("leave", Role.Friend, LeaveAsync);
        router.Add("stop", Role.Friend, StopAsync);
    }

    /// <summary><c>resolve_voice</c>: (guild, author's voice channel) for guild and DM messages.</summary>
    public static (ulong? Guild, ulong? Channel) ResolveAuthorVoice(IVoiceGateway gateway, ChatMessage message)
    {
        if (message.GuildId is { } guild)
        {
            return (guild, gateway.GetVoiceChannel(guild, message.AuthorId));
        }

        return gateway.FindUserVoice(message.AuthorId) is { } found ? (found.Guild, found.Channel) : (null, null);
    }

    private async Task JoinAsync(CommandContext c)
    {
        var (guild, channel) = ResolveAuthorVoice(voice.Gateway, c.Message);
        if (guild is null || channel is null)
        {
            await c.ReplyAsync("you're not in a voice channel");
            return;
        }

        await voice.EnsureAsync(guild.Value, channel.Value, c.Cancellation);
        await c.ReplyAsync("joined!");
    }

    private async Task LeaveAsync(CommandContext c)
    {
        // Accept a stale link (kicked / dropped) too, so the stream and session can still be cleaned up.
        var link = c.GuildId is { } g ? voice.Find(g) : voice.FindAny();
        var guild = link?.GuildId ?? c.GuildId;
        if (guild is null || (link is null && !streams.IsStreaming(guild.Value)))
        {
            await c.ReplyAsync("not in a voice channel");
            return;
        }

        _log.LogInformation("disconnecting from voice in guild {Guild}", guild);
        await streams.StopAsync(guild.Value, "leave");
        await voice.LeaveAsync(guild.Value);
        await c.ReplyAsync("left!");
    }

    private async Task StopAsync(CommandContext c)
    {
        var (guild, _) = ResolveAuthorVoice(voice.Gateway, c.Message);
        guild ??= c.GuildId ?? voice.FindAny()?.GuildId;
        if (guild is null || !streams.IsStreaming(guild.Value))
        {
            await c.ReplyAsync("nothing is playing");
            return;
        }

        _log.LogInformation("stopping stream in guild {Guild}", guild);
        await streams.StopAsync(guild.Value, "stop");
        await c.ReplyAsync("stopped");
    }

    /// <summary>
    /// Call when the voice-state cache saw <paramref name="userId"/> move from <paramref name="before"/> to
    /// <paramref name="after"/> (<see cref="Slopsoil.Commands.Voice.IVoiceGateway"/> cache already updated).
    /// Someone else leaving our channel → auto-leave when nobody but the bot remains; the bot itself leaving
    /// voice (kicked, disconnected) → tear the session down.
    /// </summary>
    public async Task OnVoiceStateChangedAsync(ulong guildId, ulong userId, ulong? before, ulong? after)
    {
        if (userId == voice.Gateway.SelfId)
        {
            if (after is null)
            {
                await OnSelfLeftAsync(guildId).ConfigureAwait(false);
            }

            return;
        }

        var link = voice.Get(guildId);
        if (link is null || before != link.ChannelId || after == link.ChannelId)
        {
            return; // only someone leaving the bot's own channel matters
        }

        var self = voice.Gateway.SelfId;
        if (voice.Gateway.UsersInChannel(guildId, link.ChannelId).Any(u => u != self))
        {
            return;
        }

        _log.LogInformation("all users left channel {Channel} in guild {Guild} — disconnecting", link.ChannelId, guildId);
        await streams.StopAsync(guildId, "auto-leave");
        await voice.LeaveAsync(guildId);
    }

    /// <summary>The bot left voice without !leave (kicked, moved out, connection lost): discard the session.</summary>
    private async Task OnSelfLeftAsync(ulong guildId)
    {
        await Task.Delay(SelfLeftGrace, _time).ConfigureAwait(false);
        if (voice.Gateway.GetVoiceChannel(guildId, voice.Gateway.SelfId) is not null || voice.IsBusy(guildId) || streams.IsStarting(guildId))
        {
            return; // moved/rejoined, or a start/join is reconnecting on purpose
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

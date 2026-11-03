using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Slopsoil.Common.Permissions;
using Slopsoil.Commands.Routing;

namespace Slopsoil.Commands.Voice;

/// <summary><c>!join</c>, <c>!leave</c>, <c>!stop</c> and auto-leave (<c>cogs/voice.py</c>).</summary>
public sealed class VoiceModule(VoiceManager voice, IStreamControl streams, ILogger<VoiceModule>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<VoiceModule>.Instance;

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
        var link = c.GuildId is { } g ? voice.Get(g) : voice.Any();
        if (link is null)
        {
            await c.ReplyAsync("not in a voice channel");
            return;
        }

        _log.LogInformation("disconnecting from voice in guild {Guild}", link.GuildId);
        await streams.StopAsync(link.GuildId, "leave");
        await voice.LeaveAsync(link.GuildId);
        await c.ReplyAsync("left!");
    }

    private async Task StopAsync(CommandContext c)
    {
        var (guild, _) = ResolveAuthorVoice(voice.Gateway, c.Message);
        guild ??= c.GuildId;
        if (guild is null || voice.Get(guild.Value) is null || !streams.IsStreaming(guild.Value))
        {
            await c.ReplyAsync("nothing is playing");
            return;
        }

        _log.LogInformation("stopping stream in guild {Guild}", guild);
        await streams.StopAsync(guild.Value, "stop");
        await c.ReplyAsync("stopped");
    }

    /// <summary>
    /// Call after the voice-state cache applied a VOICE_STATE_UPDATE for <paramref name="userId"/>: when someone
    /// other than the bot left our channel and nobody but the bot remains, stop streaming and leave.
    /// </summary>
    public async Task OnVoiceStateUpdatedAsync(ulong guildId, ulong userId, ulong? newChannelId)
    {
        var link = voice.Get(guildId);
        if (link is null || userId == voice.Gateway.SelfId || newChannelId == link.ChannelId)
        {
            return;
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
}

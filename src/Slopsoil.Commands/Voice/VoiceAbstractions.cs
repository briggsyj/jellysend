namespace Slopsoil.Commands.Voice;

/// <summary>The Discord voice operations the commands need (Host adapts GatewayClient + VoiceConnection).</summary>
public interface IVoiceGateway
{
    ulong SelfId { get; }

    /// <summary>The voice channel <paramref name="userId"/> is in within <paramref name="guildId"/>, or null.</summary>
    ulong? GetVoiceChannel(ulong guildId, ulong userId);

    /// <summary>For DM commands: the first guild where <paramref name="userId"/> is in voice.</summary>
    (ulong Guild, ulong Channel)? FindUserVoice(ulong userId);

    /// <summary>Everyone currently in the channel (voice-state cache), including the bot.</summary>
    IReadOnlyCollection<ulong> UsersInChannel(ulong guildId, ulong channelId);

    /// <summary>Join <paramref name="channelId"/> self-deafened and complete the voice handshake.</summary>
    Task<IVoiceLink> ConnectAsync(ulong guildId, ulong channelId, CancellationToken ct);
}

/// <summary>A live voice connection in one guild.</summary>
public interface IVoiceLink
{
    ulong GuildId { get; }

    ulong ChannelId { get; }

    /// <summary>False once the voice websocket has closed (kicked, moved by Discord, network).</summary>
    bool IsConnected { get; }

    /// <summary>Leave the channel. Idempotent.</summary>
    Task DisconnectAsync();
}

/// <summary>What the voice commands need from the streaming engine (implemented by the media layer).</summary>
public interface IStreamControl
{
    /// <summary>True while a stream (go-live) is running or starting in the guild.</summary>
    bool IsStreaming(ulong guildId);

    /// <summary>True while a media start is in progress (it may briefly leave and rejoin voice).</summary>
    bool IsStarting(ulong guildId);

    /// <summary>
    /// Stop everything streaming in the guild — stream task, player, audio, go-live — and clear per-session
    /// media state that ends with the voice session (playlist, SyncPlay, debug overlay). Never throws.
    /// </summary>
    Task StopAsync(ulong guildId, string reason);
}

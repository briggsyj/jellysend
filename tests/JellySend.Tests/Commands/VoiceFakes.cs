using JellySend.Commands.Voice;

namespace JellySend.Tests.Commands;

internal sealed class FakeVoiceLink(ulong guild, ulong channel) : IVoiceLink
{
    public ulong GuildId { get; } = guild;

    public ulong ChannelId { get; } = channel;

    public bool IsConnected { get; set; } = true;

    public int Disconnects { get; private set; }

    public bool ThrowOnDisconnect { get; set; }

    public Task DisconnectAsync()
    {
        Disconnects++;
        IsConnected = false;
        return ThrowOnDisconnect ? Task.FromException(new InvalidOperationException("boom")) : Task.CompletedTask;
    }
}

/// <summary>Voice states as (guild, user) → channel; bot is user 1.</summary>
internal sealed class FakeVoiceGateway : IVoiceGateway
{
    public Dictionary<(ulong Guild, ulong User), ulong> States { get; } = [];

    public List<FakeVoiceLink> Links { get; } = [];

    public ulong SelfId => 1;

    /// <summary>Voice channels per guild in Discord's order (spec 023).</summary>
    public Dictionary<ulong, List<ulong>> VoiceChannels { get; } = [];

    public Exception? VoiceChannelsError { get; set; }

    public Task<IReadOnlyList<VoiceChannelRef>> GetVoiceChannelsAsync(ulong guildId, CancellationToken ct) =>
        VoiceChannelsError is { } error ? Task.FromException<IReadOnlyList<VoiceChannelRef>>(error)
        : Task.FromResult<IReadOnlyList<VoiceChannelRef>>(
            VoiceChannels.TryGetValue(guildId, out var list) ? [.. list.Select(id => new VoiceChannelRef(id, $"voice-{id}"))] : []);

    /// <summary>The account's servers (spec 027).</summary>
    public List<ServerRef> Servers { get; } = [];

    public Exception? ServersError { get; set; }

    public Task<IReadOnlyList<ServerRef>> GetServersAsync(CancellationToken ct) =>
        ServersError is { } error ? Task.FromException<IReadOnlyList<ServerRef>>(error) : Task.FromResult<IReadOnlyList<ServerRef>>([.. Servers]);

    public ulong? GetVoiceChannel(ulong guildId, ulong userId) => States.TryGetValue((guildId, userId), out var c) ? c : null;

    public (ulong Guild, ulong Channel)? FindUserVoice(ulong userId) =>
        States.Where(kv => kv.Key.User == userId).Select(kv => ((ulong, ulong)?)(kv.Key.Guild, kv.Value)).FirstOrDefault();

    public IReadOnlyCollection<ulong> UsersInChannel(ulong guildId, ulong channelId) =>
        [.. States.Where(kv => kv.Key.Guild == guildId && kv.Value == channelId).Select(kv => kv.Key.User)];

    public Task<IVoiceLink> ConnectAsync(ulong guildId, ulong channelId, CancellationToken ct)
    {
        var link = new FakeVoiceLink(guildId, channelId);
        Links.Add(link);
        States[(guildId, SelfId)] = channelId;
        return Task.FromResult<IVoiceLink>(link);
    }
}

internal sealed class FakeStreams : IStreamControl
{
    public HashSet<ulong> Streaming { get; } = [];

    public List<(ulong Guild, string Reason)> Stops { get; } = [];

    public bool IsStreaming(ulong guildId) => Streaming.Contains(guildId);

    public HashSet<ulong> Starting { get; } = [];

    public bool IsStarting(ulong guildId) => Starting.Contains(guildId);

    public Task StopAsync(ulong guildId, string reason)
    {
        Stops.Add((guildId, reason));
        Streaming.Remove(guildId);
        return Task.CompletedTask;
    }
}

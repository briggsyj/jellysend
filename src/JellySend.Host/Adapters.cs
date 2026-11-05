using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using JellySend.Commands.Media;
using JellySend.Commands.Voice;
using JellySend.Discord.Dave;
using JellySend.Discord.Gateway;
using JellySend.Discord.GoLive;
using JellySend.Discord.Voice;
using JellySend.Services.Jellyfin;
using JellySend.Services.Media;
using JellySend.Services.SyncPlay;
using JellySend.Streaming.Engine;

namespace JellySend.Host;

/// <summary><see cref="IVoiceGateway"/> over the gateway's voice-state cache and <see cref="VoiceConnection"/>.</summary>
internal sealed class GatewayVoice(GatewayClient gateway, VoiceOptions options, JellySend.Discord.Rest.DiscordRestClient rest) : IVoiceGateway
{
    public Task<IReadOnlyList<ulong>> GetVoiceChannelsAsync(ulong guildId, CancellationToken ct) => rest.GetVoiceChannelsAsync(guildId, ct);

    public ulong SelfId => gateway.UserId;

    public ulong? GetVoiceChannel(ulong guildId, ulong userId) => gateway.VoiceStates.GetVoiceChannel(guildId, userId);

    public (ulong Guild, ulong Channel)? FindUserVoice(ulong userId) => gateway.VoiceStates.FindUser(userId);

    public IReadOnlyCollection<ulong> UsersInChannel(ulong guildId, ulong channelId) =>
        [.. gateway.VoiceStates.GetGuild(guildId).Where(kv => kv.Value == channelId).Select(kv => kv.Key)];

    public async Task<IVoiceLink> ConnectAsync(ulong guildId, ulong channelId, CancellationToken ct) =>
        new VoiceLink(await VoiceConnection.ConnectAsync(gateway, guildId, channelId, options, new VoiceStateFlags(SelfDeaf: true), ct).ConfigureAwait(false));
}

internal sealed class VoiceLink(VoiceConnection connection) : IVoiceLink
{
    private volatile bool _left;

    public VoiceConnection Connection => connection;

    public ulong GuildId => connection.GuildId;

    public ulong ChannelId => connection.ChannelId;

    public bool IsConnected => !_left && !connection.Session.Completion.IsCompleted;

    public Task DisconnectAsync()
    {
        _left = true;
        return connection.DisconnectAsync();
    }
}

/// <summary>Go-live in the guild's current voice connection.</summary>
internal sealed class GoLiveConnector(GatewayClient gateway, VoiceManager voice, VoiceOptions options) : IGoLiveConnector
{
    public async Task<IGoLiveLink> ConnectAsync(ulong guildId, CancellationToken ct)
    {
        if (voice.Get(guildId) is not VoiceLink link)
        {
            throw new InvalidOperationException("not connected to voice");
        }

        return new GoLiveLink(await GoLiveConnection.ConnectAsync(gateway, link.Connection, options, ct).ConfigureAwait(false));
    }

    private sealed class GoLiveLink(GoLiveConnection connection) : IGoLiveLink
    {
        public string Mode => connection.Mode;

        public byte[] SecretKey => connection.SecretKey;

        public uint AudioSsrc => connection.Ssrc;

        public uint VideoSsrc => connection.VideoSsrc;

        public IDaveSession? Dave => connection.DaveSession;

        public bool SendPacket(ReadOnlySpan<byte> datagram) => connection.SendPacket(datagram);

        public Task DisconnectAsync() => connection.DisconnectAsync();

        public Task Closed => connection.Session.Completion;

        public int? CloseCode => connection.Session.CloseCode;

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

/// <summary><see cref="IMediaLibrary"/> over the Jellyfin, HLS, ffprobe and SyncPlay services.</summary>
internal sealed class JellyfinLibrary(JellyfinClient client, HlsVariantResolver hls, ProbeService probe, ILoggerFactory loggers) : IMediaLibrary
{
    public bool HasUserCredentials => client.HasUserCredentials;

    public Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken ct) => client.SearchAsync(query, ct: ct);

    public Task<IReadOnlyList<MediaItem>> FindEpisodeAsync(string show, int season, int episode, CancellationToken ct) => client.FindEpisodeAsync(show, season, episode, ct);

    public Task<IReadOnlyList<MediaItem>> GetSeasonsAsync(string seriesId, CancellationToken ct) => client.GetSeasonsAsync(seriesId, ct);

    public Task<IReadOnlyList<MediaItem>> GetEpisodesAsync(string seriesId, string seasonId, CancellationToken ct) => client.GetEpisodesAsync(seriesId, seasonId, ct);

    public Task<MediaItem?> GetItemAsync(string itemId, CancellationToken ct) => client.GetItemAsync(itemId, ct);

    public Task<PlaybackStream?> StartPlaybackAsync(string itemId, TrackSelection? tracks, CancellationToken ct, bool allowDirect = true) =>
        client.StartPlaybackAsync(itemId, tracks, ct, allowDirect);

    public Task<string?> GetTranscodeStatsAsync(string itemId, CancellationToken ct) => client.GetTranscodeStatsAsync(itemId, ct);

    public Task StopEncodingAsync(string? playSessionId, CancellationToken ct) => client.StopEncodingAsync(playSessionId, ct);

    public Task<string> ResolveVariantAsync(string url, CancellationToken ct) => hls.ExtractVariantUrlAsync(url, ct);

    public Task<ProbeResult?> ProbeAsync(string url, CancellationToken ct) => probe.ProbeStreamAsync(url, ct);

    public ISyncPlay CreateSyncPlay(ulong guildId, Func<SyncPlayCommand, CancellationToken, Task> onCommand, Func<SyncPlayQueue, CancellationToken, Task> onPlayQueue) =>
        new SyncPlayAdapter(new SyncPlayGroup(client, SyncPlayGroup.DeviceIdFor(guildId), onCommand, onPlayQueue, logger: loggers.CreateLogger<SyncPlayGroup>()));

    private sealed class SyncPlayAdapter(SyncPlayGroup group) : ISyncPlay
    {
        public string? GroupName => group.GroupName;

        public bool Closed => group.Closed;

        public Task OpenAsync(string groupName, CancellationToken ct) => group.OpenAsync(groupName, ct);

        public Task CloseAsync(CancellationToken ct) => group.CloseAsync(ct);

        public Task SetQueueAsync(string itemId, double startSeconds, CancellationToken ct) => group.SetQueueAsync(itemId, startSeconds, ct);

        public Task PauseAsync(CancellationToken ct) => group.PauseAsync(ct);

        public Task UnpauseAsync(CancellationToken ct) => group.UnpauseAsync(ct);

        public Task SeekAsync(double seconds, CancellationToken ct) => group.SeekAsync(seconds, ct);
    }
}

/// <summary>Guild id → name from READY / GUILD_CREATE / GUILD_UPDATE (user READY nests it under "properties").</summary>
internal sealed class GuildNames
{
    private readonly ConcurrentDictionary<ulong, string> _names = new();

    public string? Get(ulong guildId) => _names.TryGetValue(guildId, out var n) ? n : null;

    public void Apply(string type, JsonElement data)
    {
        switch (type)
        {
            case "READY" when data.TryGetProperty("guilds", out var guilds) && guilds.ValueKind == JsonValueKind.Array:
                foreach (var g in guilds.EnumerateArray())
                {
                    Learn(g);
                }

                break;
            case "GUILD_CREATE" or "GUILD_UPDATE":
                Learn(data);
                break;
        }
    }

    private void Learn(JsonElement guild)
    {
        if (guild.ValueKind != JsonValueKind.Object || !guild.TryGetProperty("id", out var id)
            || !ulong.TryParse(id.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var guildId))
        {
            return;
        }

        var name = Name(guild) ?? (guild.TryGetProperty("properties", out var p) ? Name(p) : null);
        if (name is not null)
        {
            _names[guildId] = name;
        }
    }

    private static string? Name(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
}

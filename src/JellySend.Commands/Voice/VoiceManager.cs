using System.Collections.Concurrent;
using JellySend.Commands.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellySend.Commands.Voice;

/// <summary>
/// One voice connection per guild (<c>guild.voice_client</c>) with <c>ensure_voice</c> semantics: a link that is
/// no longer connected is discarded and replaced, a link in another channel is moved (leave + join).
/// Calls for the same guild are serialised.
/// </summary>
public sealed class VoiceManager(IVoiceGateway gateway, ILogger<VoiceManager>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<VoiceManager>.Instance;
    private readonly ConcurrentDictionary<ulong, IVoiceLink> _links = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _locks = new();

    public IVoiceGateway Gateway => gateway;

    /// <summary>The guild's link if it is still connected.</summary>
    public IVoiceLink? Get(ulong guildId) => _links.TryGetValue(guildId, out var link) && link.IsConnected ? link : null;

    /// <summary>For DM commands: any guild with a connected link.</summary>
    public IVoiceLink? Any() => _links.Values.FirstOrDefault(l => l.IsConnected);

    /// <summary>The guild's link even if its connection dropped (kicked, network) — for stop/leave commands.</summary>
    public IVoiceLink? Find(ulong guildId) => _links.TryGetValue(guildId, out var link) ? link : null;

    /// <summary>For DM stop/leave: any guild with a link, connected or not.</summary>
    public IVoiceLink? FindAny() => _links.Values.FirstOrDefault(l => l.IsConnected) ?? _links.Values.FirstOrDefault();

    /// <summary>Guilds with a voice link (connected or stale).</summary>
    public IReadOnlyCollection<ulong> Guilds => [.. _links.Keys];

    /// <summary>True while a join/move for the guild is in progress (a move leaves before it rejoins).</summary>
    public bool IsBusy(ulong guildId) => _locks.TryGetValue(guildId, out var gate) && gate.CurrentCount == 0;

    /// <summary>The server's saved default voice channel (spec 027), if any.</summary>
    public Func<ulong, ulong?>? DefaultChannel { get; set; }

    /// <summary>
    /// Where a command from <paramref name="message"/> should broadcast (spec 023): the author's voice channel; else, in a
    /// server channel, the bot's current channel there, or else the server's saved default voice channel (spec 027) or
    /// its first one (<c>Fallback</c>, with the channel's name). Null when there is nowhere (a DM from someone not in
    /// voice, or a server without voice channels).
    /// </summary>
    public async Task<(ulong Guild, ulong Channel, bool Fallback, string? Name)?> ResolveTargetAsync(ChatMessage message, CancellationToken ct)
    {
        var (guild, channel) = VoiceModule.ResolveAuthorVoice(gateway, message);
        if (guild is { } g && channel is { } ch)
        {
            return (g, ch, false, null);
        }

        if (message.GuildId is not { } server)
        {
            return null;
        }

        if (Get(server) is { } link)
        {
            return (server, link.ChannelId, false, null);
        }

        try
        {
            var channels = await gateway.GetVoiceChannelsAsync(server, ct).ConfigureAwait(false);
            var pick = DefaultChannel?.Invoke(server) is { } saved && channels.FirstOrDefault(c => c.Id == saved) is { } found
                ? found
                : channels.FirstOrDefault();
            return pick is null ? null : (server, pick.Id, true, pick.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "could not list the voice channels of guild {Guild}", server);
            return null;
        }
    }

    /// <summary>Return a link that is actually connected to <paramref name="channelId"/>.</summary>
    public async Task<IVoiceLink> EnsureAsync(ulong guildId, ulong channelId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_links.TryGetValue(guildId, out var existing))
            {
                if (existing.IsConnected && existing.ChannelId == channelId)
                {
                    return existing;
                }

                _log.LogInformation(
                    existing.IsConnected ? "moving to voice channel {Channel}" : "discarding stale voice link before reconnecting to {Channel}",
                    channelId);
                _links.TryRemove(guildId, out _);
                await SafeDisconnectAsync(existing).ConfigureAwait(false);
            }

            _log.LogInformation("connecting to voice channel {Channel} in guild {Guild}", channelId, guildId);
            var link = await gateway.ConnectAsync(guildId, channelId, ct).ConfigureAwait(false);
            _links[guildId] = link;
            return link;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Leave voice in the guild. Returns false if there was no connected link. Waits for a join/move in progress
    /// (same per-guild gate), so a leave issued while a start is still connecting removes that new link too.
    /// </summary>
    public async Task<bool> LeaveAsync(ulong guildId)
    {
        var gate = _locks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_links.TryRemove(guildId, out var link))
            {
                return false;
            }

            var wasConnected = link.IsConnected;
            await SafeDisconnectAsync(link).ConfigureAwait(false);
            return wasConnected;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SafeDisconnectAsync(IVoiceLink link)
    {
        try
        {
            await link.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "error disconnecting voice in guild {Guild}", link.GuildId);
        }
    }
}

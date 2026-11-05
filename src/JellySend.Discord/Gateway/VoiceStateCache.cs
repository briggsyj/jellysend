using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace JellySend.Discord.Gateway;

/// <summary>
/// Who is in which voice channel, per guild, maintained from gateway dispatches: READY,
/// READY_SUPPLEMENTAL and GUILD_CREATE (initial state), VOICE_STATE_UPDATE and PASSIVE_UPDATE_V2
/// (changes), GUILD_DELETE (drop).
/// </summary>
public sealed class VoiceStateCache
{
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, ulong>> _guilds = new();

    /// <summary>
    /// Raised after a VOICE_STATE_UPDATE / PASSIVE_UPDATE_V2 changed a user's channel: (guild, user, before, after).
    /// Lets listeners compare with the previous channel, which the cache no longer holds once it applied the update.
    /// </summary>
    public event Action<ulong, ulong, ulong?, ulong?>? Changed;

    /// <summary>The voice channel <paramref name="userId"/> is in, or null.</summary>
    public ulong? GetVoiceChannel(ulong guildId, ulong userId) =>
        _guilds.TryGetValue(guildId, out var users) && users.TryGetValue(userId, out var channel) ? channel : null;

    /// <summary>Snapshot of user id → channel id for one guild.</summary>
    public IReadOnlyDictionary<ulong, ulong> GetGuild(ulong guildId) =>
        _guilds.TryGetValue(guildId, out var users) ? new Dictionary<ulong, ulong>(users) : new Dictionary<ulong, ulong>();

    /// <summary>The first (lowest id) guild where <paramref name="userId"/> is in voice, for DM commands.</summary>
    public (ulong Guild, ulong Channel)? FindUser(ulong userId)
    {
        foreach (var (guild, users) in _guilds.OrderBy(g => g.Key))
        {
            if (users.TryGetValue(userId, out var channel))
            {
                return (guild, channel);
            }
        }

        return null;
    }

    public void Clear() => _guilds.Clear();

    /// <summary>Update the cache from dispatch <paramref name="eventName"/> with data <paramref name="data"/>.</summary>
    public void Apply(string eventName, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (eventName)
        {
            case "READY":
                Clear();
                ForEachGuild(data, (id, guild) => Load(id, guild, replace: true));
                break;

            case "READY_SUPPLEMENTAL":
                ForEachGuild(data, (id, guild) => Load(id, guild, replace: false));
                break;

            case "GUILD_CREATE":
                if (Snowflake(data, "id") is { } created)
                {
                    Load(created, data, replace: true);
                }

                break;

            case "GUILD_DELETE":
                if (Snowflake(data, "id") is { } deleted)
                {
                    _guilds.TryRemove(deleted, out _);
                }

                break;

            case "VOICE_STATE_UPDATE":
                if (Snowflake(data, "guild_id") is { } guildId)
                {
                    Update(guildId, data, notify: true);
                }

                break;

            case "PASSIVE_UPDATE_V2":
                if (Snowflake(data, "guild_id") is { } passive)
                {
                    var users = _guilds.GetOrAdd(passive, _ => new());
                    if (data.TryGetProperty("removed_voice_states", out var removed) && removed.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var user in removed.EnumerateArray())
                        {
                            if (Parse(user) is { } userId && users.TryRemove(userId, out var was))
                            {
                                Changed?.Invoke(passive, userId, was, null);
                            }
                        }
                    }

                    foreach (var state in Array(data, "updated_voice_states"))
                    {
                        Update(passive, state, notify: true);
                    }
                }

                break;
        }
    }

    private void Load(ulong guildId, JsonElement guild, bool replace)
    {
        if (replace)
        {
            _guilds[guildId] = new();
        }

        foreach (var state in Array(guild, "voice_states"))
        {
            Update(guildId, state);
        }
    }

    private void Update(ulong guildId, JsonElement state, bool notify = false)
    {
        if (Snowflake(state, "user_id") is not { } userId)
        {
            return;
        }

        var users = _guilds.GetOrAdd(guildId, _ => new());
        ulong? before = users.TryGetValue(userId, out var was) ? was : null;
        var after = Snowflake(state, "channel_id");
        if (after is { } channelId)
        {
            users[userId] = channelId;
        }
        else
        {
            users.TryRemove(userId, out _);
        }

        if (notify && before != after)
        {
            Changed?.Invoke(guildId, userId, before, after);
        }
    }

    private static void ForEachGuild(JsonElement data, Action<ulong, JsonElement> action)
    {
        foreach (var guild in Array(data, "guilds"))
        {
            if (Snowflake(guild, "id") is { } id)
            {
                action(id, guild);
            }
        }
    }

    private static IEnumerable<JsonElement> Array(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    internal static ulong? Snowflake(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var value) ? Parse(value) : null;

    private static ulong? Parse(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) => id,
        JsonValueKind.Number when value.TryGetUInt64(out var id) => id,
        _ => null,
    };
}

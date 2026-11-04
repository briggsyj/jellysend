using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Slopsoil.Common.Permissions;

namespace Slopsoil.Discord.Gateway;

/// <summary>
/// <see cref="IRoleDirectory"/> fed by gateway dispatches. Friends come from READY <c>relationships</c>
/// (type 1) and RELATIONSHIP_ADD/UPDATE/REMOVE. Guild membership mirrors <c>guild.get_member</c> on a
/// user account's member cache: users seen in a guild (message authors, voice states, member events),
/// dropped with GUILD_MEMBER_REMOVE / GUILD_DELETE. Subscribe <see cref="Apply"/> before the command
/// router so a message's author is known when its command runs.
/// </summary>
public sealed class GatewayRoleDirectory(Func<ulong> selfId, IReadOnlySet<ulong> adminIds) : IRoleDirectory
{
    private const int FriendRelationship = 1;

    private readonly ConcurrentDictionary<ulong, byte> _friends = new();
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, byte>> _members = new();

    public ulong SelfId => selfId();

    public IReadOnlySet<ulong> AdminIds => adminIds;

    public bool IsFriend(ulong userId) => _friends.ContainsKey(userId);

    public bool SharesGuild(ulong userId) => _members.Values.Any(m => m.ContainsKey(userId));

    public void Apply(string eventName, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (eventName)
        {
            case "READY":
                _friends.Clear();
                _members.Clear();
                foreach (var r in Array(data, "relationships"))
                {
                    ApplyRelationship(r);
                }

                foreach (var guild in Array(data, "guilds"))
                {
                    LoadGuild(guild);
                }

                break;

            case "GUILD_CREATE":
                LoadGuild(data);
                break;

            case "GUILD_DELETE":
                if (Id(data, "id") is { } gone)
                {
                    _members.TryRemove(gone, out _);
                }

                break;

            case "RELATIONSHIP_ADD":
            case "RELATIONSHIP_UPDATE":
                ApplyRelationship(data);
                break;

            case "RELATIONSHIP_REMOVE":
                if (RelationshipUser(data) is { } removed)
                {
                    _friends.TryRemove(removed, out _);
                }

                break;

            case "MESSAGE_CREATE":
                if (Id(data, "guild_id") is { } g && data.TryGetProperty("author", out var author) && Id(author, "id") is { } a)
                {
                    Guild(g)[a] = 0;
                }

                break;

            case "VOICE_STATE_UPDATE":
            case "GUILD_MEMBER_ADD":
            case "GUILD_MEMBER_UPDATE":
                if (Id(data, "guild_id") is { } guildId && MemberUser(data) is { } user)
                {
                    Guild(guildId)[user] = 0;
                }

                break;

            case "GUILD_MEMBER_REMOVE":
                if (Id(data, "guild_id") is { } left && MemberUser(data) is { } leaver && _members.TryGetValue(left, out var set))
                {
                    set.TryRemove(leaver, out _);
                }

                break;
        }
    }

    private void LoadGuild(JsonElement guild)
    {
        if (Id(guild, "id") is not { } id)
        {
            return;
        }

        var members = Guild(id);
        foreach (var m in Array(guild, "members"))
        {
            if (MemberUser(m) is { } user)
            {
                members[user] = 0;
            }
        }

        foreach (var v in Array(guild, "voice_states"))
        {
            if (Id(v, "user_id") is { } user)
            {
                members[user] = 0;
            }
        }
    }

    private void ApplyRelationship(JsonElement r)
    {
        if (RelationshipUser(r) is not { } user)
        {
            return;
        }

        if (r.TryGetProperty("type", out var t) && t.TryGetInt32(out var type) && type == FriendRelationship)
        {
            _friends[user] = 0;
        }
        else
        {
            _friends.TryRemove(user, out _);
        }
    }

    private ConcurrentDictionary<ulong, byte> Guild(ulong id) => _members.GetOrAdd(id, _ => new());

    // Relationship objects carry the other user's id as "id" (and "user_id" / "user.id" in some payloads).
    private static ulong? RelationshipUser(JsonElement r) =>
        Id(r, "user_id") ?? (r.TryGetProperty("user", out var u) ? Id(u, "id") : null) ?? Id(r, "id");

    private static ulong? MemberUser(JsonElement m) =>
        Id(m, "user_id") ?? (m.TryGetProperty("user", out var u) ? Id(u, "id") : null);

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static ulong? Id(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && ulong.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}

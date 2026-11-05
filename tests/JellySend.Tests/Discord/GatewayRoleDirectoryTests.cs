using System.Text.Json;
using JellySend.Discord.Gateway;

namespace JellySend.Tests.Discord;

public sealed class GatewayRoleDirectoryTests
{
    private readonly GatewayRoleDirectory _dir = new(() => 1, new HashSet<ulong> { 10 });

    private void Apply(string name, string json) => _dir.Apply(name, JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Self_and_admins_come_from_the_constructor()
    {
        Assert.Equal(1UL, _dir.SelfId);
        Assert.Contains(10UL, _dir.AdminIds);
    }

    [Fact]
    public void Ready_loads_friends_and_guild_members()
    {
        Apply("READY", """
            {"relationships":[{"id":"20","type":1},{"id":"21","type":2},{"user_id":"22","type":1,"id":"999"},{"user":{"id":"23"},"type":1},{"type":1}],
             "guilds":[{"id":"5","members":[{"user":{"id":"30"}},{"user_id":"31"}],"voice_states":[{"user_id":"32"}]},{"members":[{"user_id":"77"}]}]}
            """);
        Assert.True(_dir.IsFriend(20));
        Assert.False(_dir.IsFriend(21));
        Assert.True(_dir.IsFriend(22));
        Assert.False(_dir.IsFriend(999));
        Assert.True(_dir.IsFriend(23));
        Assert.True(_dir.SharesGuild(30));
        Assert.True(_dir.SharesGuild(31));
        Assert.True(_dir.SharesGuild(32));
        Assert.False(_dir.SharesGuild(77));

        Apply("READY", "{}");
        Assert.False(_dir.IsFriend(20));
        Assert.False(_dir.SharesGuild(30));
    }

    [Fact]
    public void Relationship_events_add_change_and_remove_friends()
    {
        Apply("RELATIONSHIP_ADD", """{"id":"20","type":1}""");
        Assert.True(_dir.IsFriend(20));
        Apply("RELATIONSHIP_UPDATE", """{"id":"20","type":2}""");
        Assert.False(_dir.IsFriend(20));
        Apply("RELATIONSHIP_ADD", """{"id":"20","type":1}""");
        Apply("RELATIONSHIP_REMOVE", """{"id":"20","type":1}""");
        Assert.False(_dir.IsFriend(20));
        Apply("RELATIONSHIP_REMOVE", "{}");
    }

    [Fact]
    public void Members_are_learned_from_guild_activity_and_forgotten_on_leave()
    {
        Apply("MESSAGE_CREATE", """{"guild_id":"5","author":{"id":"30"}}""");
        Apply("MESSAGE_CREATE", """{"author":{"id":"40"}}"""); // DM: not a guild member
        Apply("VOICE_STATE_UPDATE", """{"guild_id":"5","user_id":"31"}""");
        Apply("GUILD_MEMBER_ADD", """{"guild_id":"6","user":{"id":"32"}}""");
        Apply("GUILD_CREATE", """{"id":"7","members":[{"user":{"id":"33"}}]}""");
        Apply("GUILD_CREATE", """{"members":[]}""");
        Assert.True(_dir.SharesGuild(30));
        Assert.False(_dir.SharesGuild(40));
        Assert.True(_dir.SharesGuild(31));
        Assert.True(_dir.SharesGuild(32));
        Assert.True(_dir.SharesGuild(33));

        Apply("GUILD_MEMBER_REMOVE", """{"guild_id":"6","user":{"id":"32"}}""");
        Apply("GUILD_MEMBER_REMOVE", """{"guild_id":"99","user":{"id":"30"}}""");
        Apply("GUILD_DELETE", """{"id":"7"}""");
        Apply("GUILD_DELETE", "{}");
        Assert.False(_dir.SharesGuild(32));
        Assert.False(_dir.SharesGuild(33));
        Assert.True(_dir.SharesGuild(30));
    }

    [Fact]
    public void Ignores_non_objects_unknown_events_and_bad_ids()
    {
        Apply("READY", "[]");
        Apply("TYPING_START", """{"guild_id":"5","user_id":"30"}""");
        Apply("MESSAGE_CREATE", """{"guild_id":"x","author":{"id":"30"}}""");
        Apply("MESSAGE_CREATE", """{"guild_id":5,"author":{"id":"30"}}""");
        Assert.False(_dir.SharesGuild(30));
    }
}

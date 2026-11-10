namespace JellySend.Common.Permissions;

/// <summary>
/// Cascading role bits (<c>permissions.Role</c>): each role's bits are a superset of every lower role's,
/// so <c>(user &amp; required) == required</c> enforces the hierarchy. None fails every check.
/// </summary>
[Flags]
public enum Role
{
    None = 0b000,
    Viewer = 0b100,
    Friend = 0b110,
    Admin = 0b111,
}

/// <summary>What the resolver needs to know about the bot's account (fake in tests).</summary>
public interface IRoleDirectory
{
    ulong SelfId { get; }

    IReadOnlySet<ulong> AdminIds { get; }

    bool IsFriend(ulong userId);

    /// <summary>True when the user shares a guild with the bot.</summary>
    bool SharesGuild(ulong userId);
}

/// <summary>Port of <c>get_user_role</c> / <c>require_role</c>.</summary>
public static class RoleResolver
{
    public static Role Resolve(IRoleDirectory directory, ulong userId)
    {
        if (directory.AdminIds.Contains(userId))
        {
            return Role.Admin;
        }

        if (directory.IsFriend(userId))
        {
            return Role.Friend;
        }

        return directory.SharesGuild(userId) ? Role.Viewer : Role.None;
    }

    /// <summary>The bot's own account always passes; otherwise the user's role must contain all bits of <paramref name="required"/>.</summary>
    public static bool Satisfies(IRoleDirectory directory, ulong userId, Role required) =>
        userId == directory.SelfId || (Resolve(directory, userId) & required) == required;
}

using Slopsoil.Common.Permissions;

namespace Slopsoil.Tests.Common;

public sealed class RoleResolverTests
{
    private sealed class Directory : IRoleDirectory
    {
        public ulong SelfId => 1;

        public IReadOnlySet<ulong> AdminIds { get; } = new HashSet<ulong> { 10 };

        public bool IsFriend(ulong userId) => userId is 10 or 20;

        public bool SharesGuild(ulong userId) => userId is 10 or 20 or 30;
    }

    private static readonly Directory Dir = new();

    [Theory]
    [InlineData(10UL, Role.Admin)]
    [InlineData(20UL, Role.Friend)]
    [InlineData(30UL, Role.Viewer)]
    [InlineData(40UL, Role.None)]
    public void Resolve_picks_the_highest_role(ulong user, Role expected) => Assert.Equal(expected, RoleResolver.Resolve(Dir, user));

    [Theory]
    [InlineData(10UL, Role.Admin, true)]
    [InlineData(10UL, Role.Viewer, true)]
    [InlineData(20UL, Role.Friend, true)]
    [InlineData(20UL, Role.Admin, false)]
    [InlineData(30UL, Role.Viewer, true)]
    [InlineData(30UL, Role.Friend, false)]
    [InlineData(40UL, Role.Viewer, false)]
    [InlineData(40UL, Role.None, true)]
    [InlineData(1UL, Role.Admin, true)] // the bot's own account always passes
    public void Satisfies_follows_the_bit_hierarchy(ulong user, Role required, bool expected) =>
        Assert.Equal(expected, RoleResolver.Satisfies(Dir, user, required));
}

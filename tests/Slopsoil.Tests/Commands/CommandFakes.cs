using Slopsoil.Common.Permissions;
using Slopsoil.Commands.Routing;

namespace Slopsoil.Tests.Commands;

internal sealed class FakeSender : IMessageSender
{
    public List<(ulong Channel, string Content)> Sent { get; } = [];

    public Task SendAsync(ulong channelId, string content, CancellationToken ct)
    {
        lock (Sent)
        {
            Sent.Add((channelId, content));
        }

        return Task.CompletedTask;
    }

    public IEnumerable<string> Texts => Sent.Select(s => s.Content);
}

/// <summary>Self 1, admin 10, friend 20, viewer 30, stranger 40.</summary>
internal sealed class FakeRoles : IRoleDirectory
{
    public ulong SelfId => 1;

    public IReadOnlySet<ulong> AdminIds { get; } = new HashSet<ulong> { 10 };

    public bool IsFriend(ulong userId) => userId == 20;

    public bool SharesGuild(ulong userId) => userId is 20 or 30;
}

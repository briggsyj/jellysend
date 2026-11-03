namespace Slopsoil.Commands.Routing;

/// <summary>A received chat message (MESSAGE_CREATE subset).</summary>
public sealed record ChatMessage(ulong? GuildId, ulong ChannelId, ulong AuthorId, string Content);

/// <summary>Sends replies to a channel (Discord REST in production, a fake in tests).</summary>
public interface IMessageSender
{
    Task SendAsync(ulong channelId, string content, CancellationToken ct);
}

/// <summary>The invocation a handler sees: the message, the text after the command, and a reply helper.</summary>
public sealed class CommandContext(ChatMessage message, string rest, IMessageSender sender, MessageHub hub, CancellationToken ct)
{
    public ChatMessage Message { get; } = message;

    /// <summary>Everything after the command (and sub-command) name, trimmed — discord.py's <c>*, rest</c>.</summary>
    public string Rest { get; } = rest;

    public MessageHub Hub { get; } = hub;

    public CancellationToken Cancellation { get; } = ct;

    public ulong? GuildId => Message.GuildId;

    public ulong ChannelId => Message.ChannelId;

    public ulong AuthorId => Message.AuthorId;

    public Task ReplyAsync(string content) => sender.SendAsync(ChannelId, content, Cancellation);

    internal CommandContext WithRest(string newRest) => new(Message, newRest, sender, Hub, Cancellation);
}

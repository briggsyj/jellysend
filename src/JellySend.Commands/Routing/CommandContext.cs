namespace JellySend.Commands.Routing;

/// <summary>A received chat message (MESSAGE_CREATE subset).</summary>
public sealed record ChatMessage(ulong? GuildId, ulong ChannelId, ulong AuthorId, string Content);

/// <summary>Sends replies to a channel (Discord REST in production, a fake in tests).</summary>
public interface IMessageSender
{
    Task SendAsync(ulong channelId, string content, CancellationToken ct);
}

/// <summary>Shows "typing…" in a channel for about 10 seconds, or until the next message (spec 035).</summary>
public interface ITypingIndicator
{
    Task TypingAsync(ulong channelId, CancellationToken ct);
}

/// <summary>The invocation a handler sees: the message, the text after the command, and a reply helper.</summary>
public sealed class CommandContext(ChatMessage message, string rest, IMessageSender sender, MessageHub hub, CancellationToken ct)
{
    public ChatMessage Message { get; } = message;

    /// <summary>Everything after the command (and sub-command) name, trimmed — discord.py's <c>*, rest</c>.</summary>
    public string Rest { get; } = rest;

    public MessageHub Hub { get; } = hub;

    public CancellationToken Cancellation { get; } = ct;

    /// <summary>The sub-command word the user typed (alias included), or empty for the group's own handler.</summary>
    public string InvokedWith { get; private init; } = "";

    public ulong? GuildId => Message.GuildId;

    public ulong ChannelId => Message.ChannelId;

    public ulong AuthorId => Message.AuthorId;

    public Task ReplyAsync(string content) => sender.SendAsync(ChannelId, content, Cancellation);

    /// <summary>
    /// Spec 028: who may answer this context's pickers (default: the author). An event's pickers take answers from
    /// anyone allowed to <c>!play</c> in the channel.
    /// </summary>
    public Func<ChatMessage, bool>? Answerers { get; init; }

    /// <summary>How long a picker waits (default <see cref="Interaction.NumberPrompt.Timeout"/>); zero = don't wait.</summary>
    public TimeSpan? PromptTimeout { get; init; }

    /// <summary>A picker nobody answers takes its first option instead of cancelling (spec 028).</summary>
    public bool PickFirstOnTimeout { get; init; }

    /// <summary>Shared by this context and its <see cref="WithRest"/> copies: whether any picker was shown.</summary>
    public PromptLog Prompts { get; init; } = new();

    internal bool CanAnswer(ChatMessage m) =>
        m.ChannelId == ChannelId && (Answerers?.Invoke(m) ?? m.AuthorId == AuthorId);

    internal CommandContext WithRest(string newRest, string invokedWith = "") =>
        new(Message, newRest, sender, Hub, Cancellation)
        {
            InvokedWith = invokedWith,
            Answerers = Answerers,
            PromptTimeout = PromptTimeout,
            PickFirstOnTimeout = PickFirstOnTimeout,
            Prompts = Prompts,
        };
}

/// <summary>Records that a picker was shown (spec 028: an event title that needs a choice).</summary>
public sealed class PromptLog
{
    public bool Shown { get; set; }

    /// <summary>Pickers waiting for an answer right now (spec 035: the bot isn't "typing" then).</summary>
    public int Waiting;
}

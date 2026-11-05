using JellySend.Common.Permissions;

namespace JellySend.Commands.Routing;

public delegate Task CommandHandler(CommandContext context);

/// <summary>
/// <c>!</c>-prefixed command dispatch (discord.py <c>commands.Bot</c> subset): commands with aliases,
/// groups whose first word may name a sub-command (else the group's own handler gets the whole rest), names case-insensitive,
/// a global "has any role" check and per-command role checks — a failed check on a known command replies
/// <see cref="DeniedReply"/> (spec 010); unknown commands are ignored.
/// </summary>
public sealed class CommandRouter(IRoleDirectory roles, IMessageSender output, MessageHub hub, Action<string, Exception>? onError = null)
{
    /// <summary>
    /// Everything the router's commands send goes through here, so their echoes can be told apart from people: on a
    /// self-bot the account's own messages come back as messages from the same author (a prompt must not take its own
    /// question as the answer).
    /// </summary>
    private readonly SentLog sender = new(output);

    public const char Prefix = '!';

    /// <summary>
    /// Reply to a known command from someone whose role does not allow it (spec 010): a GIF link on its own, so Discord
    /// unfurls it and plays the GIF inline (no other text, no &lt;angle brackets&gt;, embeds not suppressed).
    /// </summary>
    public const string DeniedReply = "https://media3.giphy.com/media/v1.Y2lkPTc5MGI3NjExdzl4OXY0NTRkMzhudHpzeWphNmo5Y2FjbDRkeGhka3Ixb3JmemxoYSZlcD12MV9pbnRlcm5hbF9naWZfYnlfaWQmY3Q9Zw/10DVcUchEQUdFu/giphy.gif";

    private readonly Dictionary<string, Command> _commands = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Secret> _secrets = [];

    public MessageHub Hub => hub;

    /// <summary>Register a command; <paramref name="aliases"/> resolve to the same entry.</summary>
    public Command Add(string name, Role role, CommandHandler handler, params string[] aliases)
    {
        var command = new Command(name, role, handler);
        foreach (var key in (string[])[name, .. aliases])
        {
            if (!_commands.TryAdd(key, command))
            {
                throw new ArgumentException($"command '{key}' is already registered", nameof(name));
            }
        }

        return command;
    }

    /// <summary>
    /// Register a hidden phrase (spec 022), e.g. <c>!halt thee</c>: one or more words, matched case-insensitively at the
    /// start of a message, that runs the command <paramref name="target"/> with the words after it — under that command's
    /// role — after first replying <paramref name="reply"/>(those words). Phrases are never listed anywhere.
    /// </summary>
    public void AddSecret(string phrase, string target, Func<string, string> reply)
    {
        if (!_commands.ContainsKey(target))
        {
            throw new ArgumentException($"no command '{target}' for the secret '{phrase}'", nameof(target));
        }

        _secrets.Add(new Secret(phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries), target, reply));
        _secrets.Sort((a, b) => b.Words.Length.CompareTo(a.Words.Length)); // longest phrase wins
    }

    /// <summary>The secret <paramref name="text"/> starts with (whole words), and the text after it.</summary>
    private (Secret Secret, string Remainder)? MatchSecret(string text)
    {
        foreach (var secret in _secrets)
        {
            var rest = text;
            var matched = true;
            foreach (var word in secret.Words)
            {
                var (first, remainder) = SplitFirst(rest);
                if (!first.Equals(word, StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }

                rest = remainder;
            }

            if (matched)
            {
                return (secret, rest);
            }
        }

        return null;
    }

    /// <summary>Hand a message to waiters, then run it if it is a command. Returns the handler's task (completed when not a command).</summary>
    public Task DispatchAsync(ChatMessage message, CancellationToken ct)
    {
        if (sender.IsEcho(message))
        {
            return Task.CompletedTask; // our own reply coming back
        }

        hub.Publish(message);
        var content = message.Content.TrimStart();
        if (content.Length < 2 || content[0] != Prefix)
        {
            return Task.CompletedTask;
        }

        if (MatchSecret(content[1..]) is var (secret, secretRest))
        {
            var hidden = _commands[secret.Target];
            if (!RoleResolver.Satisfies(roles, message.AuthorId, Role.Viewer) || !RoleResolver.Satisfies(roles, message.AuthorId, hidden.Role))
            {
                return DenyAsync(message, ct);
            }

            var secretContext = new CommandContext(message, secretRest, sender, hub, ct);
            var run = new Command(hidden.Name, hidden.Role, async c =>
            {
                await c.ReplyAsync(secret.Reply(secretRest)).ConfigureAwait(false);
                await hidden.Handler(c).ConfigureAwait(false);
            });
            return RunAsync(run, secretContext);
        }

        var (name, rest) = SplitFirst(content[1..]);
        if (!_commands.TryGetValue(name, out var command))
        {
            return Task.CompletedTask;
        }

        // Global check (bot.add_check(has_any_role)), then the role of whatever actually runs: a group's role gates
        // only its own handler, each sub-command has its own (so `!media help` can be open to viewers).
        if (!RoleResolver.Satisfies(roles, message.AuthorId, Role.Viewer))
        {
            return DenyAsync(message, ct);
        }

        var context = new CommandContext(message, rest, sender, hub, ct);
        var (sub, subRest) = SplitFirst(rest);
        var (target, targetContext) = command.TryGetSubcommand(sub, out var subcommand)
            ? (subcommand, context.WithRest(subRest, sub))
            : (command, context);
        return RoleResolver.Satisfies(roles, message.AuthorId, target.Role) ? RunAsync(target, targetContext) : DenyAsync(message, ct);
    }

    /// <summary>A known command its author may not run: say no (unknown commands stay silent).</summary>
    private async Task DenyAsync(ChatMessage message, CancellationToken ct)
    {
        try
        {
            await sender.SendAsync(message.ChannelId, DeniedReply, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            onError?.Invoke("denied reply", ex);
        }
    }

    private async Task RunAsync(Command command, CommandContext context)
    {
        try
        {
            await command.Handler(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            onError?.Invoke(command.Name, ex);
        }
    }

    /// <summary>First whitespace-delimited word and the trimmed remainder.</summary>
    internal static (string First, string Remainder) SplitFirst(string text)
    {
        text = text.Trim();
        var i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return (text[..i], text[i..].Trim());
    }
}

/// <summary>
/// Remembers the last messages sent (channel and text) so their echo can be recognised once; bounded, oldest dropped.
/// </summary>
internal sealed class SentLog(IMessageSender inner) : IMessageSender
{
    private const int Capacity = 64;
    private readonly LinkedList<(ulong Channel, string Text)> _sent = new();

    public async Task SendAsync(ulong channelId, string content, CancellationToken ct)
    {
        lock (_sent)
        {
            // Before sending: the echo can arrive before the send returns.
            _sent.AddLast((channelId, content.Trim()));
            if (_sent.Count > Capacity)
            {
                _sent.RemoveFirst();
            }
        }

        await inner.SendAsync(channelId, content, ct).ConfigureAwait(false);
    }

    /// <summary>True (once) when <paramref name="message"/> is the echo of something sent.</summary>
    public bool IsEcho(ChatMessage message)
    {
        var text = message.Content.Trim();
        lock (_sent)
        {
            for (var node = _sent.First; node is not null; node = node.Next)
            {
                if (node.Value.Channel == message.ChannelId && node.Value.Text == text)
                {
                    _sent.Remove(node);
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>A hidden phrase for a command (spec 022), and its extra reply given the words after the phrase.</summary>
internal sealed record Secret(string[] Words, string Target, Func<string, string> Reply);

/// <summary>A registered command, optionally a group with sub-commands.</summary>
public sealed class Command(string name, Role role, CommandHandler handler)
{
    private readonly Dictionary<string, Command> _subcommands = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; } = name;

    public Role Role { get; } = role;

    public CommandHandler Handler { get; } = handler;

    public Command Sub(string subName, Role subRole, CommandHandler subHandler, params string[] aliases)
    {
        var sub = new Command($"{Name} {subName}", subRole, subHandler);
        foreach (var key in (string[])[subName, .. aliases])
        {
            if (!_subcommands.TryAdd(key, sub))
            {
                throw new ArgumentException($"sub-command '{Name} {key}' is already registered", nameof(subName));
            }
        }

        return this;
    }

    internal bool TryGetSubcommand(string word, out Command sub) => _subcommands.TryGetValue(word, out sub!);
}

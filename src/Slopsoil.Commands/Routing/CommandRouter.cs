using Slopsoil.Common.Permissions;

namespace Slopsoil.Commands.Routing;

public delegate Task CommandHandler(CommandContext context);

/// <summary>
/// <c>!</c>-prefixed command dispatch (discord.py <c>commands.Bot</c> subset): commands with aliases,
/// groups whose first word may name a sub-command (else the group's own handler gets the whole rest), names case-insensitive,
/// a global "has any role" check and per-command role checks — failures are silent, as in Python.
/// </summary>
public sealed class CommandRouter(IRoleDirectory roles, IMessageSender sender, MessageHub hub, Action<string, Exception>? onError = null)
{
    public const char Prefix = '!';

    private readonly Dictionary<string, Command> _commands = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Hand a message to waiters, then run it if it is a command. Returns the handler's task (completed when not a command).</summary>
    public Task DispatchAsync(ChatMessage message, CancellationToken ct)
    {
        hub.Publish(message);
        var content = message.Content.TrimStart();
        if (content.Length < 2 || content[0] != Prefix)
        {
            return Task.CompletedTask;
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
            return Task.CompletedTask;
        }

        var context = new CommandContext(message, rest, sender, hub, ct);
        var (sub, subRest) = SplitFirst(rest);
        var (target, targetContext) = command.TryGetSubcommand(sub, out var subcommand)
            ? (subcommand, context.WithRest(subRest, sub))
            : (command, context);
        return RoleResolver.Satisfies(roles, message.AuthorId, target.Role) ? RunAsync(target, targetContext) : Task.CompletedTask;
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

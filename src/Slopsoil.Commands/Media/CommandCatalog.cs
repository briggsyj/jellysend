using Slopsoil.Common.Permissions;

namespace Slopsoil.Commands.Media;

/// <summary>
/// One bot command (spec 008): registered as <c>!media &lt;name&gt;</c> and as the shortcut <c>!&lt;name&gt;</c>, with the
/// same aliases in both places.
/// </summary>
/// <param name="Name">Canonical name.</param>
/// <param name="Aliases">Other names, e.g. <c>ar</c> for <c>aspect-ratio</c>.</param>
/// <param name="Role">Role needed to run it.</param>
/// <param name="Help">Its help: one usage line per form.</param>
/// <param name="NeedsArgs">Run without arguments, it shows <see cref="Help"/> (nothing useful to do bare).</param>
public sealed record CommandInfo(string Name, string[] Aliases, Role Role, string Help, bool NeedsArgs = false)
{
    public IEnumerable<string> Names => [Name, .. Aliases];

    /// <summary>The reply to <c>!help &lt;name&gt;</c> and to a bare command that needs arguments.</summary>
    public string HelpText()
    {
        var aliases = Aliases.Length > 0 ? $" (also {string.Join(", ", Aliases.Select(a => $"`!{a}`"))})" : "";
        return $"**`!{Name}`**{aliases}\n{Help}\n-# also `!media {Name}`";
    }
}

/// <summary>The command list and help texts (spec 008).</summary>
public static class CommandCatalog
{
    public static readonly IReadOnlyList<CommandInfo> All =
    [
        new("play", [], Role.Friend, """
            `!play <title>` — search and play, e.g. `!play inception`
            `!play <show> s03e05` — an episode (also `3x5`, `season 3 episode 5`)
            `!play <show> s03` — pick an episode of season 3 (also `season 3`)
            `!play <title> 1:23:45` — start at a time (also `45m`, `90s`)
            `!play "<title>"` — quotes: read it all as the title
            `!play` — resume, or start the playlist
            `!play 1:23:45` — while something plays: jump to a time
            """),
        new("pause", [], Role.Friend, "`!pause` — pause (`!play` carries on)"),
        new("time", [], Role.Friend, """
            `!time` — where the stream is
            `!time <time>` — jump: `1:23:45`, `1.23.45`, `1:23` (1h 23m), `45m`, `90s`
            """),
        new("skip", [], Role.Friend, "`!skip` — play the next playlist item"),
        new("stop", [], Role.Friend, "`!stop` — stop, stay in voice"),
        new("leave", [], Role.Friend, "`!leave` — stop and leave the voice channel"),
        new("join", [], Role.Friend, "`!join` — join your voice channel"),
        new("queue", [], Role.Friend, """
            `!queue <title>` — add to the playlist (max 10, plays in order from the start)
            `!queue <show> s01e02` — add an episode
            """, NeedsArgs: true),
        new("playlist", [], Role.Friend, "`!playlist` — what's playing and up next"),
        new("order", [], Role.Friend, "`!order <# or title> <new #>` — move a playlist item", NeedsArgs: true),
        new("remove", [], Role.Friend, "`!remove <# or title>` — take an item off the playlist", NeedsArgs: true),
        new("subtitles", ["subs", "sub"], Role.Friend, """
            `!subs` — list the subtitles and pick one
            `!subs <language or #>` — e.g. `!subs english`, `!subs eng`, `!subs 2`
            `!subs off` — no subtitles
            """),
        new("audio", [], Role.Friend, """
            `!audio` — list the audio tracks and pick one
            `!audio <language or #>` — e.g. `!audio japanese`, `!audio jpn`, `!audio 2`
            """),
        new("aspect-ratio", ["aspect", "ar"], Role.Friend, """
            `!ar` — list the picture shapes and pick one
            `!ar <shape or #>` — e.g. `!ar 16:9`, `!ar widescreen`, `!ar scope`, `!ar original`
            """),
        new("resolution", ["res"], Role.Friend, """
            `!res` — list the resolutions and pick one
            `!res <height>` — `360p`, `480p`, `540p`, `720p`, `1080p`; `!res default` — back to the configured one
            Lower resolutions also lower the bitrate. Applies now, and to later items.
            """),
        new("debug", [], Role.Friend, """
            `!debug` — stream stats now
            `!debug on` / `!debug off` — stats overlay on the stream
            """),
        new("help", [], Role.Viewer, """
            `!help` — all commands
            `!help <command>` — one command, e.g. `!help play`
            """),
        new("ping", [], Role.Viewer, "`!ping` — is the bot alive?"),
    ];

    /// <summary>A command by name or alias, case-insensitive; a leading <c>!</c> or <c>media</c> is ignored.</summary>
    public static CommandInfo? Find(string text)
    {
        var words = text.Trim().TrimStart('!').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = words.Length > 1 && words[0].Equals("media", StringComparison.OrdinalIgnoreCase) ? words[1] : words.FirstOrDefault() ?? "";
        return All.FirstOrDefault(c => c.Names.Contains(name.TrimStart('!'), StringComparer.OrdinalIgnoreCase));
    }
}

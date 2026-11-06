using JellySend.Common.Permissions;

namespace JellySend.Commands.Media;

/// <summary>
/// One bot command (spec 008): registered as <c>!media &lt;name&gt;</c> and as the shortcut <c>!&lt;name&gt;</c>, with the
/// same aliases in both places.
/// </summary>
/// <param name="Name">Canonical name.</param>
/// <param name="Aliases">Other names, e.g. <c>ar</c> for <c>aspect-ratio</c>.</param>
/// <param name="Role">Role needed to run it.</param>
/// <param name="Section">Where it is listed in <c>!help all</c> (spec 019).</param>
/// <param name="Help">Its full help: what it does, one usage line per form, examples.</param>
public sealed record CommandInfo(string Name, string[] Aliases, Role Role, string Section, string Help)
{
    public IEnumerable<string> Names => [Name, .. Aliases];

    /// <summary>The reply to <c>!help &lt;name&gt;</c>.</summary>
    public string HelpText() => $"{Block()}\n-# Also works as `!media {Name}`.";

    /// <summary>This command's entry in <c>!help all</c>.</summary>
    public string Block()
    {
        var aliases = Aliases.Length > 0 ? $" (also {string.Join(", ", Aliases.Select(a => $"`!{a}`"))})" : "";
        return $"**`!{Name}`**{aliases}\n{Help}";
    }
}

/// <summary>The command list and help texts (specs 008, 019).</summary>
public static class CommandCatalog
{
    public const string Watch = "▶️ Watch";
    public const string WhileWatching = "⏯️ While watching";
    public const string Schedule = "📜 Playlist";
    public const string PictureAndSound = "🎛️ Picture & sound";
    public const string Extras = "🛠️ Extras";

    public static readonly IReadOnlyList<string> Sections = [Watch, WhileWatching, Schedule, PictureAndSound, Extras];

    public static readonly IReadOnlyList<CommandInfo> All =
    [
        new("play", [], Role.Friend, Watch, """
            Find a film or show in Jellyfin and put it on in your voice channel.
            `!play <title>` — search and play, e.g. `!play big buck bunny`
            `!play <show>` — pick a season, then an episode (or several: reply `2,5,8` or `3-6`)
            `!play <show> s2e5` — an episode; also `2x5`, `season 2 episode 5`
            `!play <show> s2` — pick an episode from season 2
            `!play <show> s1e1-3` — several episodes: the first plays, the rest are queued next. Also `s1e8-s2e2` (across seasons), `s1e1,3,5`, `season 1 episodes 1 to 3`
            `!play <title> 1:20:00` — start partway through; also `80m`, `90s`
            `!play "<title>"` — quotes: read it all as the title (for titles that look like times or episodes)
            `!play` — carry on after `!pause`, or start the playlist
            `!play 1:20:00` — while watching: jump to that time
            -# When several things match, the bot lists them: reply with a number within 30 s.
            """),
        new("pause", [], Role.Friend, WhileWatching, "`!pause` — pause the picture and sound; `!play` carries on from the same spot."),
        new("time", [], Role.Friend, WhileWatching, """
            `!time` — where we are, and how long it runs
            `!time <time>` — jump there: `1:23:45`, `1.23.45`, `1:23` (1 h 23 min), `45m`, `90s`
            """),
        new("skip", [], Role.Friend, WhileWatching, "`!skip` — stop this and play the next thing on the playlist."),
        new("stop", [], Role.Friend, WhileWatching, "`!stop` — stop the stream; the bot stays in voice and keeps the playlist (`!play` starts it again)."),
        new("leave", [], Role.Friend, WhileWatching, "`!leave` — stop and leave the voice channel. The bot also leaves on its own when the channel stays empty."),
        new("join", [], Role.Friend, WhileWatching, "`!join` — bring the bot into your voice channel (`!play` does this for you)."),
        new("playlist", [], Role.Friend, Schedule, """
            The playlist: up to 10 things that play in order, each from the start.
            `!playlist` — what's on now and coming up, numbered; then reply `add`, `remove` or `order` to change it
            `!playlist add <title>` — add to the end, e.g. `!playlist add big buck bunny`
            `!playlist add <show> s1e2` — one episode · `!playlist add <show> s1e1-6` — several
            `!playlist add <show>` — pick from the episode list; reply `2,5,8` or `3-6` for several
            `!playlist remove 2` — take item 2 off (or name it: `!playlist remove pilot`)
            `!playlist order 3 1` — move item 3 to the top, position 1 (or name it: `!playlist order pilot 2`)
            -# Leave out what comes after `add`, `remove` or `order` and the bot asks for it. Nothing playing? The first thing added starts straight away.
            """),
        new("subtitles", ["subs", "sub"], Role.Friend, PictureAndSound, """
            Subtitles, drawn onto the picture.
            `!subs` — shows the subtitles this has; reply with a number or language
            `!subs <language or #>` — e.g. `!subs english`, `!subs eng`, `!subs 2`
            `!subs off` — no subtitles
            -# The language is remembered for the next things you watch.
            """),
        new("audio", [], Role.Friend, PictureAndSound, """
            The audio track (language you hear).
            `!audio` — shows the audio tracks; reply with a number or language
            `!audio <language or #>` — e.g. `!audio japanese`, `!audio jpn`, `!audio 2`
            -# The language is remembered for the next things you watch.
            """),
        new("language", ["lang"], Role.Friend, PictureAndSound, """
            Audio + subtitles shortcut: set the language you hear and the subtitles you see in one go.
            `!language <audio> <subtitles>` — e.g. `!language japanese english` (Japanese audio, English subtitles)
            `!language <audio> off` — e.g. `!language english off` (English audio, no subtitles)
            `!language <audio>` — only change the audio
            `!language` — what's on now
            """),
        new("aspect-ratio", ["aspect", "ar"], Role.Friend, PictureAndSound, """
            Aspect ratio — the picture shape, for when it looks squashed or stretched.
            `!ar` — shows the choices; reply with a number or name
            `!ar <shape>` — `original`, `16:9` (widescreen), `4:3` (classic TV), `2.39` (cinema scope), `1.85` (theatrical)
            -# Remembered until you change it.
            """),
        new("resolution", ["res"], Role.Friend, PictureAndSound, """
            Resolution — the picture size. Smaller pictures also use less bandwidth.
            `!res` — shows the choices; reply with a number or name
            `!res <size>` — `360p`, `480p`, `540p`, `720p`, `1080p`, or `default` (the server's setting)
            -# Remembered until you change it; applies straight away.
            """),
        new("channel", [], Role.Friend, Extras, """
            Which voice channel the bot broadcasts in.
            `!channel set` — lists the voice channels the bot can reach; reply with a number to move there (what's on air carries on)
            `!channel default` — pick a server, then the voice channel the bot uses there when you're not in voice yourself
            -# With no default, the bot uses the server's first voice channel. Defaults are remembered across restarts.
            """),
        new("debug", [], Role.Friend, Extras, """
            Stream stats, for when something looks or sounds wrong.
            `!debug on` / `!debug off` — show the stats on the stream itself
            `!debug` — post the stats here once
            -# The stats show each step: Jellyfin (original file or its conversion), the bot's encoder and its speed, and the stream to Discord.
            """),
        new("help", [], Role.Viewer, Extras, """
            `!help` — the command list
            `!help <command>` — one command in detail, e.g. `!help play`
            `!help all` — every command in detail
            """),
        new("ping", [], Role.Viewer, Extras, "`!ping` — check the bot is awake."),
    ];

    /// <summary>The old playlist commands (still working); their help is <c>!playlist</c>'s.</summary>
    private static readonly string[] Renamed = ["queue", "order", "remove"];

    /// <summary>A command by name or alias, case-insensitive; a leading <c>!</c> or <c>media</c> is ignored.</summary>
    public static CommandInfo? Find(string text)
    {
        var words = text.Trim().TrimStart('!').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = words.Length > 1 && words[0].Equals("media", StringComparison.OrdinalIgnoreCase) ? words[1] : words.FirstOrDefault() ?? "";
        name = name.TrimStart('!');
        if (Renamed.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            name = "playlist"; // spec 021: !queue, !order and !remove became !playlist add / order / remove
        }

        return All.FirstOrDefault(c => c.Names.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Names that show <see cref="EventsHelp"/> with <c>!help</c> (spec 028).</summary>
    public static readonly IReadOnlyList<string> EventTopics = ["events", "event", "screenings"];

    /// <summary><c>!help events</c> (spec 028): how a Discord event starts screenings, and how to write them.</summary>
    public const string EventsHelp = """
        ### 📅 Discord Events — screenings
        Schedule a **voice channel** event and list what to play in its description, under a line `Screenings`:
        ```
        Movie night!

        Screenings:
        1. Big Buck Bunny
        2. Example Show s1e1-3
        ```
        • Each line is written like `!play`: a title, an episode (`show s2e5`) or several (`show s1e1-3`).
        • Number them (`1.`), bullet them (`-`), or just put one per line. `Screenings` can be any case; the colon is optional. The list ends at a blank line.
        • At the start time the bot starts the event and streams the list in the event's voice channel, taking over anything playing.
        • It posts each item's start time and the end time in that channel's chat (in your own time zone).
        • A title with several matches is asked about in that chat — anyone allowed to `!play` can answer before it's due; otherwise the first match plays.
        • When the last one ends, the bot ends the event.
        -# The event's creator must be allowed to use `!play`. The bot needs **Manage Events** to start and end the event.
        """;

    /// <summary>Discord's message limit, with room to spare.</summary>
    public const int MessageLimit = 1900;

    /// <summary>
    /// <c>!help all</c> (spec 019): every command in detail, one message per section (a section too long for one message
    /// continues in the next).
    /// </summary>
    public static IReadOnlyList<string> FullGuide()
    {
        var messages = new List<string>();
        foreach (var section in Sections)
        {
            var current = $"### {section}";
            foreach (var command in All.Where(c => c.Section == section))
            {
                var block = command.Block();
                if (current.Length + block.Length + 2 > MessageLimit)
                {
                    messages.Add(current);
                    current = $"### {section} (continued)";
                }

                current += "\n" + block + "\n";
            }

            messages.Add(current.TrimEnd());
        }

        messages.Add(EventsHelp);
        messages[0] = "## 📺 JellySend — the full guide\n" + messages[0];
        messages[^1] += "\n-# Every command also works as `!media <command>`, e.g. `!media play`."
            + "\n-# JellySend is a fork of Slopsoil, rewritten in .NET · free software under the GPL-3.0.";
        return messages;
    }
}

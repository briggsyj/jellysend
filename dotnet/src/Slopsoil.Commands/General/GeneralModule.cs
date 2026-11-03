using Slopsoil.Common.Permissions;
using Slopsoil.Commands.Routing;

namespace Slopsoil.Commands.General;

/// <summary><c>!ping</c>, <c>!hello</c> and <c>!help</c> (<c>cogs/general.py</c>, Jellyfin edition).</summary>
public static class GeneralModule
{
    public static void Register(CommandRouter router, Func<bool> jellyfinAvailable)
    {
        router.Add("ping", Role.Viewer, c => c.ReplyAsync("pong!"));
        router.Add("hello", Role.Admin, c => c.ReplyAsync("Hello, world!"));
        router.Add("help", Role.Viewer, c => c.ReplyAsync(HelpText(jellyfinAvailable())));
    }

    public static string HelpText(bool jellyfin)
    {
        List<string> lines =
        [
            "**General**",
            "  `!ping` — check if the bot is alive",
            "  `!help` — show this message",
            "",
            "**Voice**",
            "  `!join` — join your current voice channel",
            "  `!leave` — disconnect from voice",
            "  `!stop` — stop the current stream",
        ];
        if (jellyfin)
        {
            lines.AddRange(
            [
                "",
                "**Jellyfin**",
                "  `!media <title>` — search Jellyfin for a movie, series, or episode and select it for playback",
            ]);
        }

        return string.Join('\n', lines);
    }
}

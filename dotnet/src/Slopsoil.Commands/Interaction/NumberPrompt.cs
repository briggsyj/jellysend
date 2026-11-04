using System.Globalization;
using Slopsoil.Commands.Routing;

namespace Slopsoil.Commands.Interaction;

/// <summary>Port of <c>_wait_for_number</c>: the invoking user picks 1..max in the same channel within 30 s (Python: 10 s).</summary>
public static class NumberPrompt
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The choice, or null after replying "timed out — cancelled", "cancelled" or an out-of-range message.</summary>
    public static async Task<int?> AskAsync(CommandContext context, int max)
    {
        bool Check(ChatMessage m)
        {
            var text = m.Content.Trim();
            return m.AuthorId == context.AuthorId && m.ChannelId == context.ChannelId
                && (IsDecimal(text) || text.StartsWith(CommandRouter.Prefix));
        }

        var reply = await context.Hub.WaitForAsync(Check, Timeout, context.Cancellation).ConfigureAwait(false);
        if (reply is null)
        {
            await context.ReplyAsync("timed out — cancelled").ConfigureAwait(false);
            return null;
        }

        var content = reply.Content.Trim();
        if (content.StartsWith(CommandRouter.Prefix))
        {
            // A new command cancels the prompt but still runs through the router.
            await context.ReplyAsync("cancelled").ConfigureAwait(false);
            return null;
        }

        if (!int.TryParse(content, NumberStyles.None, CultureInfo.InvariantCulture, out var choice) || choice < 1 || choice > max)
        {
            await context.ReplyAsync($"invalid selection — pick a number between 1 and {max}").ConfigureAwait(false);
            return null;
        }

        return choice;
    }

    /// <summary>Python <c>str.isdecimal()</c> restricted to ASCII digits (Discord digits are ASCII in practice).</summary>
    private static bool IsDecimal(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
}

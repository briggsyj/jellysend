using System.Globalization;
using Slopsoil.Commands.Routing;

namespace Slopsoil.Commands.Interaction;

/// <summary>Port of <c>_wait_for_number</c>: the invoking user picks 1..max in the same channel within 30 s (Python: 10 s).</summary>
public static class NumberPrompt
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The choice, or null after replying "timed out — cancelled", "cancelled" or an out-of-range message.
    /// <paramref name="match"/> also accepts a reply that names an option (spec 008: <c>english</c>, <c>16:9</c>) — it
    /// returns the 1-based choice, or null for chat that names nothing (ignored, like any other chat).
    /// </summary>
    public static async Task<int?> AskAsync(CommandContext context, int max, Func<string, int?>? match = null)
    {
        int? Named(string text) => match?.Invoke(text) is { } n && n >= 1 && n <= max ? n : null;

        bool Check(ChatMessage m)
        {
            var text = m.Content.Trim();
            return m.AuthorId == context.AuthorId && m.ChannelId == context.ChannelId
                && (IsDecimal(text) || text.StartsWith(CommandRouter.Prefix) || Named(text) is not null);
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

        if (!IsDecimal(content) && Named(content) is { } named)
        {
            return named;
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

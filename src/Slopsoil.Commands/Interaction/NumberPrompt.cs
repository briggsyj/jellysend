using System.Globalization;
using Slopsoil.Commands.Routing;

namespace Slopsoil.Commands.Interaction;

/// <summary>Port of <c>_wait_for_number</c>: the invoking user picks 1..max in the same channel within 30 s (Python: 10 s).</summary>
public static partial class NumberPrompt
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

    /// <summary>
    /// Like <see cref="AskAsync"/>, but the reply may pick several, in order (spec 014): <c>2,5,8</c>, <c>3-6</c>,
    /// <c>1, 3-5, 9</c>. Duplicates are dropped. Null after replying "timed out — cancelled", "cancelled" or an
    /// invalid-selection message.
    /// </summary>
    public static async Task<IReadOnlyList<int>?> AskManyAsync(CommandContext context, int max)
    {
        bool Check(ChatMessage m)
        {
            var text = m.Content.Trim();
            return m.AuthorId == context.AuthorId && m.ChannelId == context.ChannelId
                && (IsDecimal(text) || IsList(text) || text.StartsWith(CommandRouter.Prefix));
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
            await context.ReplyAsync("cancelled").ConfigureAwait(false);
            return null;
        }

        if (ParseList(content, max) is not { } picks)
        {
            await context.ReplyAsync($"invalid selection — pick numbers between 1 and {max} (e.g. `2`, `2,5,8` or `3-6`)").ConfigureAwait(false);
            return null;
        }

        return picks;
    }

    /// <summary>
    /// <c>2,5,8</c> / <c>3-6</c> / <c>1, 3-5, 9</c> → the 1-based picks in order without duplicates; null when malformed,
    /// out of 1..<paramref name="max"/>, or a range runs backwards.
    /// </summary>
    public static IReadOnlyList<int>? ParseList(string text, int max)
    {
        if (!IsList(text) && !IsDecimal(text.Trim()))
        {
            return null;
        }

        var picks = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (!int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                || !int.TryParse(bounds[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var to)
                || from < 1 || to > max || from > to)
            {
                return null;
            }

            for (var n = from; n <= to; n++)
            {
                if (!picks.Contains(n))
                {
                    picks.Add(n);
                }
            }
        }

        return picks;
    }

    private static bool IsList(string text) => ListPattern().IsMatch(text.Trim());

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9]+(\s*-\s*[0-9]+)?(\s*,\s*[0-9]+(\s*-\s*[0-9]+)?)*$")]
    private static partial System.Text.RegularExpressions.Regex ListPattern();
}

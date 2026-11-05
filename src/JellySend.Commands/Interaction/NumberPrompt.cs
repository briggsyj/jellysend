using System.Globalization;
using JellySend.Commands.Routing;

namespace JellySend.Commands.Interaction;

/// <summary>Port of <c>_wait_for_number</c>: the invoking user picks 1..max in the same channel within 30 s (Python: 10 s).</summary>
public static partial class NumberPrompt
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The context's own wait (spec 028), else <see cref="Timeout"/>.</summary>
    private static TimeSpan WaitFor(CommandContext context) => context.PromptTimeout ?? Timeout;

    /// <summary>
    /// The choice, or null after replying "⌛ No answer — cancelled.", "Cancelled." or an out-of-range message.
    /// <paramref name="match"/> also accepts a reply that names an option (spec 008: <c>english</c>, <c>16:9</c>) — it
    /// returns the 1-based choice, or null for chat that names nothing (ignored, like any other chat).
    /// </summary>
    public static async Task<int?> AskAsync(CommandContext context, int max, Func<string, int?>? match = null)
    {
        int? Named(string text) => match?.Invoke(text) is { } n && n >= 1 && n <= max ? n : null;

        bool Check(ChatMessage m)
        {
            var text = m.Content.Trim();
            return context.CanAnswer(m)
                && (IsDecimal(text) || text.StartsWith(CommandRouter.Prefix) || Named(text) is not null);
        }

        context.Prompts.Shown = true;
        var reply = await context.Hub.WaitForAsync(Check, WaitFor(context), context.Cancellation).ConfigureAwait(false);
        if (reply is null)
        {
            if (context.PickFirstOnTimeout)
            {
                await context.ReplyAsync("⌛ No answer — going with the first one.").ConfigureAwait(false);
                return 1;
            }

            await context.ReplyAsync("⌛ No answer — cancelled.").ConfigureAwait(false);
            return null;
        }

        var content = reply.Content.Trim();
        if (content.StartsWith(CommandRouter.Prefix))
        {
            // A new command cancels the prompt but still runs through the router.
            await context.ReplyAsync("Cancelled.").ConfigureAwait(false);
            return null;
        }

        if (!IsDecimal(content) && Named(content) is { } named)
        {
            return named;
        }

        if (!int.TryParse(content, NumberStyles.None, CultureInfo.InvariantCulture, out var choice) || choice < 1 || choice > max)
        {
            await context.ReplyAsync($"🙅 That's not on the list — pick a number between 1 and {max}.").ConfigureAwait(false);
            return null;
        }

        return choice;
    }

    /// <summary>
    /// An optional menu (spec 021): waits for the invoking user to reply with one of <paramref name="words"/>
    /// (case-insensitive) and returns it. Other chat is ignored; a timeout or a new command ends it silently.
    /// </summary>
    public static async Task<string?> AskWordAsync(CommandContext context, IReadOnlyList<string> words)
    {
        string? Word(string text) => words.FirstOrDefault(w => w.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));

        var reply = await context.Hub.WaitForAsync(
            m => context.CanAnswer(m)
                && (Word(m.Content) is not null || m.Content.TrimStart().StartsWith(CommandRouter.Prefix)),
            WaitFor(context), context.Cancellation).ConfigureAwait(false);
        return reply is null ? null : Word(reply.Content);
    }

    /// <summary>
    /// Free text from the invoking user in the same channel within <see cref="Timeout"/> (spec 021: the arguments of a
    /// playlist action picked from the <c>!playlist</c> menu). Null after replying "⌛ No answer — cancelled." or, when the
    /// reply is a new command (which still runs), "Cancelled.".
    /// </summary>
    public static async Task<string?> AskTextAsync(CommandContext context)
    {
        var reply = await context.Hub.WaitForAsync(
            m => context.CanAnswer(m) && m.Content.Trim().Length > 0,
            WaitFor(context), context.Cancellation).ConfigureAwait(false);
        if (reply is null)
        {
            await context.ReplyAsync("⌛ No answer — cancelled.").ConfigureAwait(false);
            return null;
        }

        var content = reply.Content.Trim();
        if (content.StartsWith(CommandRouter.Prefix))
        {
            await context.ReplyAsync("Cancelled.").ConfigureAwait(false);
            return null;
        }

        return content;
    }

    /// <summary>Python <c>str.isdecimal()</c> restricted to ASCII digits (Discord digits are ASCII in practice).</summary>
    private static bool IsDecimal(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    /// <summary>
    /// Like <see cref="AskAsync"/>, but the reply may pick several, in order (spec 014): <c>2,5,8</c>, <c>3-6</c>,
    /// <c>1, 3-5, 9</c>. Duplicates are dropped. Null after replying "⌛ No answer — cancelled.", "Cancelled." or an
    /// invalid-selection message.
    /// </summary>
    public static async Task<IReadOnlyList<int>?> AskManyAsync(CommandContext context, int max)
    {
        bool Check(ChatMessage m)
        {
            var text = m.Content.Trim();
            return context.CanAnswer(m)
                && (IsDecimal(text) || IsList(text) || text.StartsWith(CommandRouter.Prefix));
        }

        context.Prompts.Shown = true;
        var reply = await context.Hub.WaitForAsync(Check, WaitFor(context), context.Cancellation).ConfigureAwait(false);
        if (reply is null)
        {
            if (context.PickFirstOnTimeout)
            {
                await context.ReplyAsync("⌛ No answer — going with the first one.").ConfigureAwait(false);
                return [1];
            }

            await context.ReplyAsync("⌛ No answer — cancelled.").ConfigureAwait(false);
            return null;
        }

        var content = reply.Content.Trim();
        if (content.StartsWith(CommandRouter.Prefix))
        {
            await context.ReplyAsync("Cancelled.").ConfigureAwait(false);
            return null;
        }

        if (ParseList(content, max) is not { } picks)
        {
            await context.ReplyAsync($"🙅 That's not on the list — pick numbers between 1 and {max} (e.g. `2`, `2,5,8` or `3-6`).").ConfigureAwait(false);
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

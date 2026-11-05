using System.Globalization;
using System.Text.Json;

namespace JellySend.Commands.Routing;

/// <summary>MESSAGE_CREATE → <see cref="ChatMessage"/>. Bot authors are skipped (<c>process_commands</c>).</summary>
public static class ChatMessageParser
{
    /// <summary>
    /// Parse synchronously inside the dispatch handler: the gateway disposes the JSON document after
    /// handlers return, so the element must not be captured by later work.
    /// </summary>
    public static bool TryParse(JsonElement data, out ChatMessage message)
    {
        message = null!;
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String
            || Id(data, "channel_id") is not { } channel
            || !data.TryGetProperty("author", out var author) || Id(author, "id") is not { } authorId
            || (author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True))
        {
            return false;
        }

        message = new ChatMessage(Id(data, "guild_id"), channel, authorId, content.GetString()!);
        return true;
    }

    private static ulong? Id(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && ulong.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}

using System.Globalization;
using System.Text.Json;

namespace JellySend.Discord.Voice;

/// <summary>Small readers for Discord JSON (snowflakes arrive as strings, sometimes as numbers).</summary>
internal static class VoiceJson
{
    public static ulong? Snowflake(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String when ulong.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) => id,
            JsonValueKind.Number when v.TryGetUInt64(out var id) => id,
            _ => null,
        };
    }

    public static string? String(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static int Int(JsonElement obj, string name, int fallback = 0) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.TryGetInt32(out var i)
            ? i
            : fallback;

    /// <summary>Endpoint without the scheme (discord.py-self strips <c>wss://</c> before adding its own).</summary>
    public static string StripScheme(string endpoint) =>
        endpoint.StartsWith("wss://", StringComparison.Ordinal) ? endpoint[6..] : endpoint;

    public static string Id(ulong id) => id.ToString(CultureInfo.InvariantCulture);
}

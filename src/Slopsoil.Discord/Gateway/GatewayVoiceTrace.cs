using System.Text.Json;

namespace Slopsoil.Discord.Gateway;

/// <summary>
/// One log line for each main-gateway event about the bot's own voice connection or screenshare (spec 012):
/// VOICE_STATE_UPDATE (self), VOICE_SERVER_UPDATE, STREAM_CREATE / STREAM_SERVER_UPDATE / STREAM_UPDATE /
/// STREAM_DELETE. Tokens are never included; session ids are shortened.
/// </summary>
public static class GatewayVoiceTrace
{
    /// <param name="ownSessionId">This bot's gateway session id: a self voice state with another session id means
    /// another Discord client on the same account took the voice connection.</param>
    public static string? Describe(string type, JsonElement d, ulong selfId, string? ownSessionId = null)
    {
        if (d.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        switch (type)
        {
            case "VOICE_STATE_UPDATE":
                if (Str(d, "user_id") != selfId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    return null;
                }

                var session = Str(d, "session_id");
                var other = !string.IsNullOrEmpty(ownSessionId) && session is not null && session != ownSessionId
                    ? " — ANOTHER DISCORD CLIENT on this account (phone/desktop/browser) owns this voice state"
                    : "";
                return $"self VOICE_STATE_UPDATE guild={Str(d, "guild_id") ?? "-"} channel={Str(d, "channel_id") ?? "none (left)"} "
                    + $"session={Short(session)} self_stream={Bool(d, "self_stream")} self_video={Bool(d, "self_video")} "
                    + $"mute={Bool(d, "mute")}/{Bool(d, "self_mute")} deaf={Bool(d, "deaf")}/{Bool(d, "self_deaf")}{other}";
            case "VOICE_SERVER_UPDATE":
                return $"VOICE_SERVER_UPDATE guild={Str(d, "guild_id") ?? "-"} endpoint={Str(d, "endpoint") ?? "null (awaiting)"}";
            case "STREAM_CREATE":
            case "STREAM_UPDATE":
                return $"{type} stream_key={Str(d, "stream_key") ?? "-"} rtc_server_id={Str(d, "rtc_server_id") ?? "-"} "
                    + $"paused={Bool(d, "paused")} viewers={Count(d, "viewer_ids")} region={Str(d, "region") ?? "-"}";
            case "STREAM_SERVER_UPDATE":
                return $"STREAM_SERVER_UPDATE stream_key={Str(d, "stream_key") ?? "-"} endpoint={Str(d, "endpoint") ?? "-"}";
            case "STREAM_DELETE":
                return $"STREAM_DELETE stream_key={Str(d, "stream_key") ?? "-"} reason={Str(d, "reason") ?? "-"} unavailable={Bool(d, "unavailable")}";
            default:
                return null;
        }
    }

    private static string? Str(JsonElement d, string name) =>
        d.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static string Bool(JsonElement d, string name) =>
        d.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "-",
        } : "-";

    private static string Count(JsonElement d, string name) =>
        d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";

    private static string Short(string? id) => id is { Length: > 8 } ? id[..8] + "…" : id ?? "-";
}

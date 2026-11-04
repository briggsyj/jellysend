namespace Slopsoil.Discord.Voice;

/// <summary>Voice gateway v8 opcodes (discord.py-self <c>DiscordVoiceWebSocket</c>).</summary>
public static class VoiceOp
{
    public const int Identify = 0;
    public const int SelectProtocol = 1;
    public const int Ready = 2;
    public const int Heartbeat = 3;
    public const int SessionDescription = 4;
    public const int Speaking = 5;
    public const int HeartbeatAck = 6;
    public const int Resume = 7;
    public const int Hello = 8;
    public const int Resumed = 9;
    public const int ClientsConnect = 11;
    public const int Video = 12;
    public const int ClientDisconnect = 13;
    public const int VoiceBackendVersion = 16;
    public const int DavePrepareTransition = 21;
    public const int DaveExecuteTransition = 22;
    public const int DaveTransitionReady = 23;
    public const int DavePrepareEpoch = 24;
    public const int MlsExternalSender = 25;
    public const int MlsKeyPackage = 26;
    public const int MlsProposals = 27;
    public const int MlsCommitWelcome = 28;
    public const int MlsAnnounceCommitTransition = 29;
    public const int MlsWelcome = 30;
    public const int MlsInvalidCommitWelcome = 31;

    /// <summary>A readable name for logs.</summary>
    public static string Name(int op) => op switch
    {
        Identify => "IDENTIFY",
        SelectProtocol => "SELECT_PROTOCOL",
        Ready => "READY",
        Heartbeat => "HEARTBEAT",
        SessionDescription => "SESSION_DESCRIPTION",
        Speaking => "SPEAKING",
        HeartbeatAck => "HEARTBEAT_ACK",
        Resume => "RESUME",
        Hello => "HELLO",
        Resumed => "RESUMED",
        ClientsConnect => "CLIENTS_CONNECT",
        Video => "VIDEO",
        ClientDisconnect => "CLIENT_DISCONNECT",
        VoiceBackendVersion => "VOICE_BACKEND_VERSION",
        DavePrepareTransition => "DAVE_PREPARE_TRANSITION",
        DaveExecuteTransition => "DAVE_EXECUTE_TRANSITION",
        DaveTransitionReady => "DAVE_TRANSITION_READY",
        DavePrepareEpoch => "DAVE_PREPARE_EPOCH",
        MlsExternalSender => "MLS_EXTERNAL_SENDER",
        MlsKeyPackage => "MLS_KEY_PACKAGE",
        MlsProposals => "MLS_PROPOSALS",
        MlsCommitWelcome => "MLS_COMMIT_WELCOME",
        MlsAnnounceCommitTransition => "MLS_ANNOUNCE_COMMIT_TRANSITION",
        MlsWelcome => "MLS_WELCOME",
        MlsInvalidCommitWelcome => "MLS_INVALID_COMMIT_WELCOME",
        _ => "?",
    };
}

/// <summary>Discord voice-gateway close codes, for logs.</summary>
public static class VoiceCloseCodes
{
    public static string Describe(int? code) => code switch
    {
        null => "no close code",
        1000 => "normal closure",
        1001 => "going away",
        1006 => "abnormal closure (connection dropped)",
        4001 => "unknown opcode",
        4002 => "failed to decode payload",
        4003 => "not authenticated",
        4004 => "authentication failed",
        4005 => "already authenticated",
        4006 => "session no longer valid",
        4009 => "session timeout",
        4011 => "server not found",
        4012 => "unknown protocol",
        4014 => "disconnected (kicked, channel deleted, or the main gateway session dropped / moved elsewhere)",
        4015 => "voice server crashed",
        4016 => "unknown encryption mode",
        4017 => "E2EE/DAVE protocol required",
        4020 => "bad request",
        4021 => "disconnected: rate limited",
        4022 => "disconnected: call terminated (channel deleted, voice server changed, or the call was ended)",
        _ => "unknown code",
    };
}

/// <summary>A safe one-line summary of a voice-gateway JSON payload: ids, modes, versions — never keys or tokens.</summary>
public static class VoiceTrace
{
    public static string Summary(int op, System.Text.Json.JsonElement d)
    {
        if (d.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var name in (string[])["ssrc", "mode", "dave_protocol_version", "protocol_version", "transition_id", "epoch", "user_id", "heartbeat_interval", "video_codec", "audio_codec"])
        {
            if (d.TryGetProperty(name, out var v) && v.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number)
            {
                parts.Add($"{name}={v}");
            }
        }

        if (d.TryGetProperty("user_ids", out var ids) && ids.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            parts.Add($"user_ids={ids.GetArrayLength()}");
        }

        if (d.TryGetProperty("modes", out var modes) && modes.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            parts.Add($"modes={string.Join(",", modes.EnumerateArray().Select(m => m.ToString()))}");
        }

        return parts.Count == 0 ? "" : " " + string.Join(" ", parts);
    }
}

using System.Text.Json;

namespace JellySend.Discord.Gateway;

/// <summary>
/// The parts of the main Discord gateway that voice and go-live need. Implemented by
/// <see cref="GatewayClient"/>; faked in voice/go-live tests.
/// </summary>
public interface IGatewayClient
{
    /// <summary>The logged-in user's id (from READY).</summary>
    ulong UserId { get; }

    /// <summary>The gateway session id (from READY) — voice IDENTIFY needs it.</summary>
    string SessionId { get; }

    /// <summary>Send a raw gateway payload <c>{"op": op, "d": data}</c>.</summary>
    Task SendAsync(int op, object? data, CancellationToken ct);

    /// <summary>Raised for every dispatch (op 0): event name ("t") and data ("d").</summary>
    event Action<string, JsonElement>? Dispatch;
}

/// <summary>Gateway opcodes used by the subset.</summary>
public static class GatewayOp
{
    public const int Dispatch = 0;
    public const int Heartbeat = 1;
    public const int Identify = 2;
    public const int PresenceUpdate = 3;
    public const int VoiceStateUpdate = 4;
    public const int Resume = 6;
    public const int Reconnect = 7;
    public const int InvalidSession = 9;
    public const int Hello = 10;
    public const int HeartbeatAck = 11;
    public const int StreamCreate = 18;
    public const int StreamDelete = 19;
    public const int StreamWatch = 20;
    public const int StreamSetPaused = 22;
    public const int GuildSubscriptionsBulk = 37;
}

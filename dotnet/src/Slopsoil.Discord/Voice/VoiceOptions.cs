using Slopsoil.Discord.Dave;
using Slopsoil.Discord.Transport;

namespace Slopsoil.Discord.Voice;

/// <summary>Transports, DAVE and timing shared by voice and go-live connections.</summary>
public sealed class VoiceOptions
{
    public required IWebSocketFactory WebSockets { get; init; }

    public required IUdpFactory Udp { get; init; }

    /// <summary>Creates a DAVE session on the first SESSION_DESCRIPTION / epoch with DAVE enabled. Null disables DAVE.</summary>
    public Func<IDaveSession>? DaveSessionFactory { get; init; }

    /// <summary>Highest DAVE protocol version to advertise in IDENTIFY (libdave's max; 0 without a factory).</summary>
    public int MaxDaveProtocolVersion { get; init; } = 1;

    /// <summary>Bound on the whole connect (gateway dispatches + voice handshake), as <c>golive.py</c>'s 30 s.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a graceful websocket close may take on disconnect.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Clock for heartbeat timestamps, heartbeat delays and timeouts.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Optional diagnostic sink (never receives tokens or keys).</summary>
    public Action<string>? Log { get; init; }

    internal int EffectiveMaxDaveProtocolVersion => DaveSessionFactory is null ? 0 : MaxDaveProtocolVersion;
}

/// <summary>Raised when a voice websocket closes or the handshake cannot complete.</summary>
public sealed class VoiceConnectionException(string message, int? closeCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? CloseCode { get; } = closeCode;
}

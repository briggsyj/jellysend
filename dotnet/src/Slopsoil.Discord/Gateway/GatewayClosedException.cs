namespace Slopsoil.Discord.Gateway;

/// <summary>The gateway closed with a code that forbids reconnecting (e.g. 4004 authentication failed).</summary>
public sealed class GatewayClosedException(int code)
    : Exception($"Discord gateway closed with code {code}; not reconnecting.")
{
    public int Code { get; } = code;
}

using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;

namespace JellySend.Discord.Transport;

/// <summary><see cref="IUdpConnection"/> over a real non-blocking <see cref="UdpClient"/> (live-network adapter).</summary>
[ExcludeFromCodeCoverage]
public sealed class UdpClientConnection : IUdpConnection
{
    private readonly UdpClient _client = new(AddressFamily.InterNetwork);

    public void Connect(string host, int port)
    {
        _client.Connect(host, port);
        _client.Client.Blocking = false;
    }

    public bool Send(ReadOnlySpan<byte> datagram)
    {
        try
        {
            return _client.Client.Send(datagram, SocketFlags.None) == datagram.Length;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.NoBufferSpaceAvailable)
        {
            return false;
        }
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct) => (await _client.ReceiveAsync(ct).ConfigureAwait(false)).Buffer;

    public void Dispose() => _client.Dispose();
}

/// <summary>Creates <see cref="UdpClientConnection"/>s.</summary>
[ExcludeFromCodeCoverage]
public sealed class UdpClientFactory : IUdpFactory
{
    public IUdpConnection Create() => new UdpClientConnection();
}

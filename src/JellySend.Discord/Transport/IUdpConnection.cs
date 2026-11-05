namespace JellySend.Discord.Transport;

/// <summary>Seam over a connected UDP socket (voice media + IP discovery).</summary>
public interface IUdpConnection : IDisposable
{
    void Connect(string host, int port);

    /// <summary>Best-effort send; returns false if the packet was dropped (e.g. send buffer full).</summary>
    bool Send(ReadOnlySpan<byte> datagram);

    Task<byte[]> ReceiveAsync(CancellationToken ct);
}

public interface IUdpFactory
{
    IUdpConnection Create();
}

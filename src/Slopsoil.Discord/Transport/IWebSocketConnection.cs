namespace Slopsoil.Discord.Transport;

/// <summary>A message received from a websocket.</summary>
public readonly record struct WebSocketFrame(bool IsText, ReadOnlyMemory<byte> Data, bool IsClose = false, int? CloseCode = null, string? CloseReason = null)
{
    public string Text => System.Text.Encoding.UTF8.GetString(Data.Span);
}

/// <summary>Seam over <see cref="System.Net.WebSockets.ClientWebSocket"/> so protocol code is testable.</summary>
public interface IWebSocketConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);

    Task SendTextAsync(string json, CancellationToken ct);

    Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>Next complete message; a frame with <c>IsClose</c> when the peer closed.</summary>
    Task<WebSocketFrame> ReceiveAsync(CancellationToken ct);

    Task CloseAsync(int code, CancellationToken ct);
}

public interface IWebSocketFactory
{
    IWebSocketConnection Create();
}

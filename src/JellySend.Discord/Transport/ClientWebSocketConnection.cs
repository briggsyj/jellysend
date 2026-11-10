using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;

namespace JellySend.Discord.Transport;

/// <summary>
/// <see cref="IWebSocketConnection"/> over <see cref="ClientWebSocket"/>: reassembles fragmented
/// messages and reports the peer's close as a frame. Live-network adapter, so excluded from coverage.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ClientWebSocketConnection : IWebSocketConnection
{
    /// <summary>Upper bound for one reassembled message (user-account READY payloads run to a few MB).</summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    private const int ChunkBytes = 64 * 1024;

    private readonly ClientWebSocket _socket = new();

    public ClientWebSocketConnection(IReadOnlyDictionary<string, string>? headers = null)
    {
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            _socket.Options.SetRequestHeader(name, value);
        }
    }

    public Task ConnectAsync(Uri uri, CancellationToken ct) => _socket.ConnectAsync(uri, ct);

    public Task SendTextAsync(string json, CancellationToken ct) =>
        _socket.SendAsync(new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, endOfMessage: true, ct).AsTask();

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
        _socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, ct).AsTask();

    public async Task<WebSocketFrame> ReceiveAsync(CancellationToken ct)
    {
        var message = new ArrayBufferWriter<byte>(ChunkBytes);
        while (true)
        {
            var result = await _socket.ReceiveAsync(message.GetMemory(ChunkBytes), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                var code = (int?)_socket.CloseStatus;
                var reason = _socket.CloseStatusDescription;
                try
                {
                    // Complete the closing handshake; the peer may already be gone.
                    await _socket.CloseOutputAsync(_socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure, null, ct)
                        .ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }

                return new WebSocketFrame(false, ReadOnlyMemory<byte>.Empty, IsClose: true, CloseCode: code, CloseReason: reason);
            }

            message.Advance(result.Count);
            if (message.WrittenCount > MaxMessageBytes)
            {
                throw new WebSocketException(WebSocketError.Faulted, "Websocket message exceeds the size limit.");
            }

            if (result.EndOfMessage)
            {
                return new WebSocketFrame(result.MessageType == WebSocketMessageType.Text, message.WrittenMemory.ToArray());
            }
        }
    }

    public async Task CloseAsync(int code, CancellationToken ct)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket.CloseOutputAsync((WebSocketCloseStatus)code, null, ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Creates <see cref="ClientWebSocketConnection"/>s with fixed request headers.</summary>
[ExcludeFromCodeCoverage]
public sealed class ClientWebSocketFactory(IReadOnlyDictionary<string, string>? headers = null) : IWebSocketFactory
{
    public IWebSocketConnection Create() => new ClientWebSocketConnection(headers);
}

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;

namespace JellySend.Services.SyncPlay;

/// <summary>Kind of message received from the Jellyfin websocket.</summary>
public enum SocketMessageType
{
    /// <summary>A text (JSON) message.</summary>
    Text,

    /// <summary>A binary message (ignored by SyncPlay).</summary>
    Binary,

    /// <summary>The peer closed the connection.</summary>
    Close,
}

/// <summary>One complete websocket message.</summary>
/// <param name="Type">Message kind.</param>
/// <param name="Text">UTF-8 text for <see cref="SocketMessageType.Text"/>, otherwise empty.</param>
public readonly record struct SocketMessage(SocketMessageType Type, string Text = "");

/// <summary>Seam over <see cref="ClientWebSocket"/> for the Jellyfin <c>/socket</c> endpoint.</summary>
public interface ISyncPlaySocket : IAsyncDisposable
{
    /// <summary>Open the connection with extra request headers.</summary>
    Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct);

    /// <summary>Send one text message.</summary>
    Task SendTextAsync(string text, CancellationToken ct);

    /// <summary>Next complete message (a <see cref="SocketMessageType.Close"/> message when the peer closed).</summary>
    Task<SocketMessage> ReceiveAsync(CancellationToken ct);

    /// <summary>Start a normal close.</summary>
    Task CloseAsync(CancellationToken ct);
}

/// <summary>
/// <see cref="ISyncPlaySocket"/> over <see cref="ClientWebSocket"/> with a 30 s keep-alive ping (aiohttp
/// <c>heartbeat=30</c>). Live-network adapter, so excluded from coverage.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ClientSyncPlaySocket : ISyncPlaySocket
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;

    private readonly ClientWebSocket _socket = new();

    /// <inheritdoc/>
    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        foreach (var (name, value) in headers)
        {
            _socket.Options.SetRequestHeader(name, value);
        }

        return _socket.ConnectAsync(uri, ct);
    }

    /// <inheritdoc/>
    public Task SendTextAsync(string text, CancellationToken ct) =>
        _socket.SendAsync(new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, endOfMessage: true, ct).AsTask();

    /// <inheritdoc/>
    public async Task<SocketMessage> ReceiveAsync(CancellationToken ct)
    {
        var message = new ArrayBufferWriter<byte>(4096);
        while (true)
        {
            var result = await _socket.ReceiveAsync(message.GetMemory(4096), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new SocketMessage(SocketMessageType.Close);
            }

            message.Advance(result.Count);
            if (message.WrittenCount > MaxMessageBytes)
            {
                throw new WebSocketException(WebSocketError.Faulted, "Websocket message exceeds the size limit.");
            }

            if (result.EndOfMessage)
            {
                return result.MessageType == WebSocketMessageType.Text
                    ? new SocketMessage(SocketMessageType.Text, Encoding.UTF8.GetString(message.WrittenSpan))
                    : new SocketMessage(SocketMessageType.Binary);
            }
        }
    }

    /// <inheritdoc/>
    public async Task CloseAsync(CancellationToken ct)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

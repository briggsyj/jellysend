using System.Text.Json;
using System.Threading.Channels;
using JellySend.Services.SyncPlay;

namespace JellySend.Tests.Services;

/// <summary>Scripted Jellyfin websocket.</summary>
internal sealed class FakeSyncPlaySocket : ISyncPlaySocket
{
    private readonly Channel<SocketMessage> _inbound = Channel.CreateUnbounded<SocketMessage>();
    private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();

    public Uri? Uri { get; private set; }

    public IReadOnlyDictionary<string, string>? Headers { get; private set; }

    public Exception? ConnectError { get; set; }

    public Exception? SendError { get; set; }

    public Exception? CloseError { get; set; }

    public int CloseCalls { get; private set; }

    public bool Disposed { get; private set; }

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        Uri = uri;
        Headers = headers;
        return ConnectError is null ? Task.CompletedTask : Task.FromException(ConnectError);
    }

    public Task SendTextAsync(string text, CancellationToken ct)
    {
        if (SendError is not null)
        {
            return Task.FromException(SendError);
        }

        _sent.Writer.TryWrite(text);
        return Task.CompletedTask;
    }

    public Task<SocketMessage> ReceiveAsync(CancellationToken ct) => _inbound.Reader.ReadAsync(ct).AsTask();

    public Task CloseAsync(CancellationToken ct)
    {
        CloseCalls++;
        return CloseError is null ? Task.CompletedTask : Task.FromException(CloseError);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    // ---- server side ----

    public void Push(string json) => _inbound.Writer.TryWrite(new SocketMessage(SocketMessageType.Text, json));

    public void Push(object message) => Push(JsonSerializer.Serialize(message));

    public void PushBinary() => _inbound.Writer.TryWrite(new SocketMessage(SocketMessageType.Binary));

    public void PeerClose() => _inbound.Writer.TryWrite(new SocketMessage(SocketMessageType.Close));

    public void Break(Exception? error = null) => _inbound.Writer.TryComplete(error ?? new IOException("connection reset"));

    public async Task<string> NextSentAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _sent.Reader.ReadAsync(cts.Token);
    }

    public bool HasSent => _sent.Reader.TryPeek(out _);
}

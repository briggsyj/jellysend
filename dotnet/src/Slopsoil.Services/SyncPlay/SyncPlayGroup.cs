using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Slopsoil.Common.Logging;
using Slopsoil.Services.Jellyfin;

namespace Slopsoil.Services.SyncPlay;

/// <summary>A <c>SyncPlayCommand</c> from another group member.</summary>
/// <param name="Command"><c>Pause</c>, <c>Unpause</c>, <c>Seek</c> or <c>Stop</c> (passed through verbatim).</param>
/// <param name="PositionSeconds">Position the command applies at, if the server sent one.</param>
public sealed record SyncPlayCommand(string Command, double? PositionSeconds);

/// <summary>A <c>PlayQueue</c> group update: what the group is now playing.</summary>
/// <param name="ItemId">The playing item, or null when the index is out of range / missing.</param>
/// <param name="StartSeconds">Start position (0 when missing or invalid).</param>
/// <param name="IsPlaying">Whether the group is playing (vs. paused).</param>
public sealed record SyncPlayQueue(string? ItemId, double StartSeconds, bool IsPlaying);

/// <summary>
/// Jellyfin SyncPlay group member (port of <c>services/syncplay.py</c>). <see cref="OpenAsync"/> signs in under a
/// per-guild device id (a Jellyfin session can be in only one group), opens <c>/socket</c>, creates a group and
/// sets IgnoreWait so the group never waits for the bot to buffer. Bot commands are forwarded with
/// <see cref="SetQueueAsync"/> / <see cref="PauseAsync"/> / <see cref="UnpauseAsync"/> / <see cref="SeekAsync"/>;
/// other members' commands and queue changes arrive through the handlers. When the websocket drops the group
/// marks itself <see cref="Closed"/> so the next play opens a fresh one.
/// </summary>
public sealed class SyncPlayGroup : IAsyncDisposable
{
    /// <summary>Timeout for each SyncPlay REST call.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly string KeepAliveMessage = """{"MessageType":"KeepAlive"}""";

    private readonly JellyfinClient _client;
    private readonly Func<SyncPlayCommand, CancellationToken, Task> _onCommand;
    private readonly Func<SyncPlayQueue, CancellationToken, Task> _onPlayQueue;
    private readonly Func<ISyncPlaySocket> _socketFactory;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _keepAliveLock = new();
    private ISyncPlaySocket? _socket;
    private string? _authorization;
    private volatile bool _closed;
    private volatile bool _socketOpen;

    /// <summary>Create a (not yet open) group member.</summary>
    /// <param name="client">Jellyfin client with user credentials.</param>
    /// <param name="deviceId">Per-guild device id, see <see cref="DeviceIdFor"/>.</param>
    /// <param name="onCommand">Called for each SyncPlayCommand from the group (on the reader; awaited in order).</param>
    /// <param name="onPlayQueue">Called for each PlayQueue update (on the reader; awaited in order).</param>
    /// <param name="socketFactory">Websocket seam; <see cref="ClientSyncPlaySocket"/> when null.</param>
    /// <param name="logger">Logger; <see cref="NullLogger"/> when null.</param>
    /// <param name="time">Clock for keep-alives; the client's clock when null.</param>
    public SyncPlayGroup(
        JellyfinClient client,
        string deviceId,
        Func<SyncPlayCommand, CancellationToken, Task> onCommand,
        Func<SyncPlayQueue, CancellationToken, Task> onPlayQueue,
        Func<ISyncPlaySocket>? socketFactory = null,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        _client = client;
        DeviceId = deviceId;
        _onCommand = onCommand;
        _onPlayQueue = onPlayQueue;
        _socketFactory = socketFactory ?? (() => new ClientSyncPlaySocket());
        _log = logger ?? NullLogger.Instance;
        _time = time ?? client.Time;
    }

    /// <summary>Device id this member signs in with.</summary>
    public string DeviceId { get; }

    /// <summary>Group id from the server's <c>GroupJoined</c> update, once received.</summary>
    public string? GroupId { get; private set; }

    /// <summary>Group name, set once <see cref="OpenAsync"/> succeeded.</summary>
    public string? GroupName { get; private set; }

    /// <summary>True after <see cref="CloseAsync"/> or once the websocket dropped; a closed group is never reused.</summary>
    public bool Closed => _closed;

    /// <summary>The websocket reader, once open (tests await it).</summary>
    internal Task? Reader { get; private set; }

    /// <summary>The keep-alive loop, once the server asked for one.</summary>
    internal Task? KeepAlive { get; private set; }

    /// <summary>Per-guild device id: <c>slopsoil-&lt;guildId&gt;</c>.</summary>
    public static string DeviceIdFor(ulong guildId) => $"slopsoil-{guildId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Group name shown in Jellyfin apps: <c>slopsoil · &lt;guild name&gt;</c>.</summary>
    public static string GroupNameFor(string guildName) => $"slopsoil · {guildName}";

    /// <summary>
    /// Sign in, connect <c>/socket?api_key=&lt;token&gt;&amp;deviceId=&lt;id&gt;</c>, start the reader, then
    /// <c>POST /SyncPlay/New</c> and <c>/SyncPlay/SetIgnoreWait</c>.
    /// </summary>
    /// <exception cref="JellyfinException">Sign-in or a SyncPlay request failed (call <see cref="CloseAsync"/> afterwards).</exception>
    public async Task OpenAsync(string groupName, CancellationToken ct = default)
    {
        var session = await _client.AuthenticateAsync(DeviceId, ct).ConfigureAwait(false);
        _authorization = AuthHeader.Build(session.AccessToken, DeviceId);
        var wsUrl = ReplaceFirst(ReplaceFirst(_client.BaseUrl, "https://", "wss://"), "http://", "ws://")
            + $"/socket?api_key={session.AccessToken}&deviceId={DeviceId}";
        var socket = _socketFactory();
        _socket = socket;
        var headers = new Dictionary<string, string> { ["Authorization"] = _authorization };
        try
        {
            await socket.ConnectAsync(new Uri(wsUrl), headers, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new JellyfinException($"SyncPlay websocket connect to {Redactor.RedactUrl(wsUrl)} failed", ex);
        }

        _socketOpen = true;
        Reader = Task.Run(() => ReadLoopAsync(socket), CancellationToken.None);
        await PostAsync("/SyncPlay/New", new JsonObject { ["GroupName"] = groupName }, ct).ConfigureAwait(false);
        await PostAsync("/SyncPlay/SetIgnoreWait", new JsonObject { ["IgnoreWait"] = true }, ct).ConfigureAwait(false);
        GroupName = groupName;
        _log.LogInformation("SyncPlay: created group {GroupName}", groupName);
    }

    /// <summary>Leave the group (best effort), stop the reader and keep-alive, close the socket. Idempotent.</summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            await PostAsync("/SyncPlay/Leave", null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JellyfinException or InvalidOperationException)
        {
            _log.LogDebug("SyncPlay: leave failed: {Error}", Redactor.RedactText(ex.Message));
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        await ReleaseSocketAsync(graceful: true).ConfigureAwait(false);
        _log.LogInformation("SyncPlay: left group {GroupName}", GroupName);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    /// <summary>Replace the group's queue with <paramref name="itemId"/> starting at <paramref name="startSeconds"/>.</summary>
    /// <exception cref="InvalidOperationException">The group is not open.</exception>
    /// <exception cref="JellyfinException">The request failed.</exception>
    public Task SetQueueAsync(string itemId, double startSeconds = 0, CancellationToken ct = default) =>
        PostAsync(
            "/SyncPlay/SetNewQueue",
            new JsonObject
            {
                ["PlayingQueue"] = new JsonArray(itemId),
                ["PlayingItemPosition"] = 0,
                ["StartPositionTicks"] = JellyfinTicks.FromSeconds(startSeconds),
            },
            ct);

    /// <summary>Ask the group to pause.</summary>
    /// <exception cref="InvalidOperationException">The group is not open.</exception>
    /// <exception cref="JellyfinException">The request failed.</exception>
    public Task PauseAsync(CancellationToken ct = default) => PostAsync("/SyncPlay/Pause", null, ct);

    /// <summary>Ask the group to resume.</summary>
    /// <exception cref="InvalidOperationException">The group is not open.</exception>
    /// <exception cref="JellyfinException">The request failed.</exception>
    public Task UnpauseAsync(CancellationToken ct = default) => PostAsync("/SyncPlay/Unpause", null, ct);

    /// <summary>Ask the group to seek to <paramref name="seconds"/>.</summary>
    /// <exception cref="InvalidOperationException">The group is not open.</exception>
    /// <exception cref="JellyfinException">The request failed.</exception>
    public Task SeekAsync(double seconds, CancellationToken ct = default) =>
        PostAsync("/SyncPlay/Seek", new JsonObject { ["PositionTicks"] = JellyfinTicks.FromSeconds(seconds) }, ct);

    /// <summary><c>ticks_to_seconds</c>: integer ticks (number or numeric string) → seconds; null otherwise.</summary>
    internal static double? TicksToSeconds(JsonElement ticks)
    {
        switch (ticks.ValueKind)
        {
            case JsonValueKind.Number when ticks.TryGetInt64(out var n):
                return JellyfinTicks.ToSeconds(n);
            case JsonValueKind.Number:
                return JellyfinTicks.ToSeconds((long)Math.Truncate(ticks.GetDouble()));
            case JsonValueKind.String when long.TryParse(ticks.GetString()!.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var s):
                return JellyfinTicks.ToSeconds(s);
            default:
                return null;
        }
    }

    /// <summary>Dispatch one websocket message (<c>handle_message</c>).</summary>
    internal async Task HandleMessageAsync(JsonElement msg, CancellationToken ct)
    {
        if (msg.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var kind = msg.TryGetProperty("MessageType", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        var data = msg.TryGetProperty("Data", out var d) ? d : default;
        switch (kind)
        {
            case "ForceKeepAlive":
                StartKeepAlive(TimeSpan.FromSeconds(Math.Max(5.0, KeepAliveTimeout(data) / 2)));
                break;
            case "SyncPlayCommand" when data.ValueKind == JsonValueKind.Object:
                if (data.TryGetProperty("Command", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } command)
                {
                    var position = data.TryGetProperty("PositionTicks", out var p) ? TicksToSeconds(p) : null;
                    await _onCommand(new SyncPlayCommand(command, position), ct).ConfigureAwait(false);
                }

                break;
            case "SyncPlayGroupUpdate" when data.ValueKind == JsonValueKind.Object:
                await HandleGroupUpdateAsync(data, ct).ConfigureAwait(false);
                break;
        }
    }

    private static double KeepAliveTimeout(JsonElement data) => data.ValueKind switch
    {
        JsonValueKind.Number when data.GetDouble() != 0 => data.GetDouble(),
        JsonValueKind.String when data.GetString()!.Length > 0 => double.Parse(data.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
        JsonValueKind.True => 1,
        JsonValueKind.Object or JsonValueKind.Array => throw new FormatException("ForceKeepAlive data is not a number"),
        _ => 60,
    };

    private static string ReplaceFirst(string s, string from, string to)
    {
        var i = s.IndexOf(from, StringComparison.Ordinal);
        return i < 0 ? s : string.Concat(s.AsSpan(0, i), to, s.AsSpan(i + from.Length));
    }

    private async Task HandleGroupUpdateAsync(JsonElement update, CancellationToken ct)
    {
        var kind = update.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var payload = update.TryGetProperty("Data", out var d) ? d : default;
        switch (kind)
        {
            case "GroupJoined" when payload.ValueKind == JsonValueKind.Object:
                GroupId = payload.TryGetProperty("GroupId", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
                break;
            case "PlayQueue" when payload.ValueKind == JsonValueKind.Object:
                string? itemId = null;
                var index = 0;
                var indexValid = !payload.TryGetProperty("PlayingItemIndex", out var i)
                    || (i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out index));
                if (indexValid
                    && payload.TryGetProperty("Playlist", out var playlist)
                    && playlist.ValueKind == JsonValueKind.Array
                    && index >= 0
                    && index < playlist.GetArrayLength()
                    && playlist[index] is { ValueKind: JsonValueKind.Object } entry
                    && entry.TryGetProperty("ItemId", out var id)
                    && id.ValueKind == JsonValueKind.String)
                {
                    itemId = id.GetString();
                }

                var start = payload.TryGetProperty("StartPositionTicks", out var s) ? TicksToSeconds(s) ?? 0.0 : 0.0;
                var playing = payload.TryGetProperty("IsPlaying", out var pl) && pl.ValueKind == JsonValueKind.True;
                await _onPlayQueue(new SyncPlayQueue(itemId, start, playing), ct).ConfigureAwait(false);
                break;
            case "GroupLeft" or "NotInGroup" or "GroupDoesNotExist":
                _log.LogInformation("SyncPlay: {Update} for group {GroupName}", kind, GroupName);
                break;
            case "CreateGroupDenied" or "JoinGroupDenied" or "LibraryAccessDenied":
                _log.LogWarning("SyncPlay: {Update} — check the Jellyfin user's SyncPlay permission", kind);
                break;
        }
    }

    private void StartKeepAlive(TimeSpan interval)
    {
        lock (_keepAliveLock)
        {
            if (KeepAlive is null || KeepAlive.IsCompleted)
            {
                KeepAlive = KeepAliveLoopAsync(interval, _cts.Token);
            }
        }
    }

    /// <summary>Send a KeepAlive every <paramref name="interval"/> until the socket closes or a send fails.</summary>
    private async Task KeepAliveLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (_socketOpen && _socket is { } socket)
            {
                await Task.Delay(interval, _time, ct).ConfigureAwait(false);
                try
                {
                    await socket.SendTextAsync(KeepAliveMessage, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogDebug("SyncPlay: keepalive failed: {Error}", ex.Message);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task ReadLoopAsync(ISyncPlaySocket socket)
    {
        var ct = _cts.Token;
        try
        {
            while (true)
            {
                var message = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                if (message.Type == SocketMessageType.Close)
                {
                    break;
                }

                if (message.Type != SocketMessageType.Text)
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(message.Text);
                    await HandleMessageAsync(doc.RootElement, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogError(ex, "SyncPlay: error handling message");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // closed by CloseAsync
        }
        catch (Exception ex)
        {
            _log.LogWarning("SyncPlay: websocket reader ended: {Error}", Redactor.RedactText(ex.Message));
        }

        _socketOpen = false;
        if (!_closed)
        {
            // Connection lost (server restart, network blip): mark the group dead so the next play opens a fresh one.
            _log.LogWarning("SyncPlay: websocket closed for group {GroupName}", GroupName);
            _closed = true;
            await _cts.CancelAsync().ConfigureAwait(false);
            await ReleaseSocketAsync(graceful: false).ConfigureAwait(false);
        }
    }

    private async Task ReleaseSocketAsync(bool graceful)
    {
        _socketOpen = false;
        _authorization = null;
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket is null)
        {
            return;
        }

        if (graceful)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2), _time);
                await socket.CloseAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug("SyncPlay: websocket close failed: {Error}", ex.Message);
            }
        }

        await socket.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PostAsync(string path, JsonNode? body, CancellationToken ct)
    {
        var authorization = _authorization ?? throw new InvalidOperationException("SyncPlay group is not open");
        using var timeoutCts = new CancellationTokenSource(RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, _client.BaseUrl + path)
        {
            Content = new StringContent((body ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        try
        {
            using var response = await _client.Http.SendAsync(request, linked.Token).ConfigureAwait(false);
            if ((int)response.StatusCode >= 400)
            {
                var text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                throw new JellyfinException($"SyncPlay {path} → HTTP {(int)response.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new JellyfinException($"SyncPlay {path} timed out", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new JellyfinException($"SyncPlay {path} failed", ex);
        }
    }
}

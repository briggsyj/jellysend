using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JellySend.Discord.Transport;

namespace JellySend.Discord.Gateway;

/// <summary>
/// Discord user-account gateway client (v9, JSON, per-payload zlib compression), modelled on <c>discord.py-self</c>'s
/// <c>DiscordWebSocket</c>: IDENTIFY with client properties/capabilities/presence/client_state,
/// heartbeat with ACK-based zombie detection, RESUME via <c>resume_gateway_url</c>, RECONNECT and
/// INVALID_SESSION handling, exponential reconnect backoff and a send rate limit.
/// </summary>
public sealed class GatewayClient : IGatewayClient, IAsyncDisposable
{
    public const int ApiVersion = 9;

    /// <summary>Close codes after which <c>discord.py-self</c> does not reconnect.</summary>
    private static readonly int[] FatalCloseCodes = [4004, 4010, 4011, 4012, 4013, 4014];

    /// <summary>Invalid seq / session timed out: reconnect with a fresh IDENTIFY.</summary>
    private static readonly int[] ReidentifyCloseCodes = [4007, 4009];

    private static readonly JsonElement Null = JsonDocument.Parse("null").RootElement;

    private readonly IWebSocketFactory _factory;
    private readonly GatewayOptions _options;
    private readonly TimeProvider _time;
    private readonly GatewayRateLimiter _limiter;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _token = string.Empty;
    private Task? _run;
    private volatile IWebSocketConnection? _socket;
    private volatile string? _sessionId;
    private volatile Uri? _resumeUrl;
    private long _sequence = -1;
    private long _userId;
    private int _healthy;

    /// <summary>Guilds subscribed to on this session (spec 024).</summary>
    private readonly HashSet<ulong> _subscribed = [];

    /// <summary>Set once Discord rejected a subscription payload: never send another (spec 024).</summary>
    private volatile bool _subscriptionsRejected;

    /// <summary>A subscription was sent on the current connection (a 4001/4002 close then means Discord refused it).</summary>
    private volatile bool _subscribedThisConnection;
    private int _disposed;

    public GatewayClient(IWebSocketFactory factory, GatewayOptions? options = null)
    {
        _factory = factory;
        _options = options ?? new GatewayOptions();
        _time = _options.Time;
        _limiter = new GatewayRateLimiter(_options.RateLimitCount, _options.RateLimitWindow, _time);
    }

    public ulong UserId => (ulong)Interlocked.Read(ref _userId);

    public string SessionId => _sessionId ?? string.Empty;

    /// <summary>Last dispatch sequence number, or null before the first one.</summary>
    public long? Sequence => Interlocked.Read(ref _sequence) is var s and >= 0 ? s : null;

    /// <summary>The <c>resume_gateway_url</c> from READY, if any.</summary>
    public Uri? ResumeUrl => _resumeUrl;

    public VoiceStateCache VoiceStates { get; } = new();

    /// <summary>Completes when the client stops; faults with <see cref="GatewayClosedException"/> on a fatal close.</summary>
    public Task Completion => _run ?? Task.CompletedTask;

    public event Action<string, JsonElement>? Dispatch;

    public ulong? GetVoiceChannel(ulong guildId, ulong userId) => VoiceStates.GetVoiceChannel(guildId, userId);

    /// <summary>
    /// Connect and IDENTIFY with <paramref name="token"/>; returns after READY. The connection is then
    /// kept alive (resumed / re-identified) in the background until <see cref="DisposeAsync"/>.
    /// </summary>
    public async Task ConnectAsync(string token, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (_run is not null)
        {
            throw new InvalidOperationException("The gateway client is already connected.");
        }

        _token = token;
        _run = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        try
        {
            await _ready.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task SendAsync(int op, object? data, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["op"] = op,
            ["d"] = data switch
            {
                null => null,
                JsonNode node => node.Parent is null ? node : node.DeepClone(),
                _ => JsonSerializer.SerializeToNode(data, data.GetType()),
            },
        };
        await _limiter.WaitAsync(ct).ConfigureAwait(false);
        await SendRawAsync(_socket, payload, ct).ConfigureAwait(false);
    }

    /// <summary>Op 4: join, move or leave (<paramref name="channelId"/> null) voice in a guild.</summary>
    public Task UpdateVoiceStateAsync(
        ulong? guildId, ulong? channelId, bool selfMute, bool selfDeaf, bool selfVideo, CancellationToken ct) =>
        SendAsync(GatewayOp.VoiceStateUpdate, new JsonObject
        {
            ["guild_id"] = guildId?.ToString(CultureInfo.InvariantCulture),
            ["channel_id"] = channelId?.ToString(CultureInfo.InvariantCulture),
            ["self_mute"] = selfMute,
            ["self_deaf"] = selfDeaf,
            ["self_video"] = selfVideo,
        }, ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_socket is { } socket)
        {
            await CloseQuietlyAsync(socket, 1000).ConfigureAwait(false);
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already surfaced through Completion / ConnectAsync.
            }
        }

        _stop.Dispose();
    }

    private enum Outcome
    {
        /// <summary>Reconnect now and RESUME if a session exists.</summary>
        Resume,

        /// <summary>Drop the session, wait 1–5 s, reconnect and IDENTIFY.</summary>
        Reidentify,

        /// <summary>Connection failed: back off, then reconnect (RESUME if possible).</summary>
        Retry,
    }

    private sealed class Connection(IWebSocketConnection socket, CancellationTokenSource cts)
    {
        public IWebSocketConnection Socket { get; } = socket;

        public CancellationTokenSource Cts { get; } = cts;

        public Task Heartbeat { get; set; } = Task.CompletedTask;

        public bool Started { get; set; }

        public volatile bool Acked = true;

        public volatile bool Zombie;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var failures = 0;
        try
        {
            while (true)
            {
                Outcome outcome;
                try
                {
                    outcome = await RunConnectionAsync(stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not GatewayClosedException && !stop.IsCancellationRequested)
                {
                    Log($"Gateway connection failed: {ex.GetType().Name}: {ex.Message}");
                    outcome = Outcome.Retry;
                }

                if (Interlocked.Exchange(ref _healthy, 0) == 1)
                {
                    failures = 0;
                }

                switch (outcome)
                {
                    case Outcome.Reidentify:
                        ResetSession();
                        await Task.Delay(TimeSpan.FromSeconds(1 + (4 * _options.Jitter())), _time, stop).ConfigureAwait(false);
                        break;

                    case Outcome.Retry:
                        var delay = TimeSpan.FromSeconds(Math.Min(_options.MaxBackoff.TotalSeconds, Math.Pow(2, failures)));
                        failures++;
                        Log($"Reconnecting to the gateway in {delay.TotalSeconds:0.##} s.");
                        await Task.Delay(delay, _time, stop).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            _ready.TrySetCanceled(stop);
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            throw;
        }
    }

    private async Task<Outcome> RunConnectionAsync(CancellationToken stop)
    {
        var resume = _sessionId is not null && Sequence is not null;
        var url = BuildUrl(resume ? _resumeUrl ?? _options.GatewayUrl : _options.GatewayUrl);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var socket = _factory.Create();
        var connection = new Connection(socket, cts);
        try
        {
            await socket.ConnectAsync(url, cts.Token).ConfigureAwait(false);
            _socket = socket;
            Log($"Connected to {url.Host} ({(resume ? "resuming" : "identifying")}).");
            while (true)
            {
                WebSocketFrame frame;
                try
                {
                    frame = await socket.ReceiveAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (connection.Zombie && !stop.IsCancellationRequested)
                {
                    await CloseQuietlyAsync(socket, 4000).ConfigureAwait(false);
                    return Outcome.Resume;
                }

                if (frame.IsClose)
                {
                    return Classify(frame.CloseCode);
                }

                if (await HandleAsync(frame, connection, resume).ConfigureAwait(false) is { } outcome)
                {
                    await CloseQuietlyAsync(socket, outcome == Outcome.Reidentify ? 1000 : 4000).ConfigureAwait(false);
                    return outcome;
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_socket, socket))
            {
                _socket = null;
            }

            await cts.CancelAsync().ConfigureAwait(false);
            await connection.Heartbeat.ConfigureAwait(false);
            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Outcome Classify(int? code)
    {
        Log($"Gateway closed with code {code?.ToString(CultureInfo.InvariantCulture) ?? "none"}.");
        if (code is 4001 or 4002 && _subscribedThisConnection && !_subscriptionsRejected)
        {
            // Unknown opcode / decode error right after subscribing: Discord didn't take op 37. Stop sending it, so
            // reconnecting can't loop.
            _subscriptionsRejected = true;
            Log("Discord rejected the guild subscriptions; continuing without them.");
        }

        _subscribedThisConnection = false;
        if (code is { } c && FatalCloseCodes.Contains(c))
        {
            throw new GatewayClosedException(c);
        }

        return code is { } r && ReidentifyCloseCodes.Contains(r) ? Outcome.Reidentify : Outcome.Retry;
    }

    /// <summary>Decompress a per-payload zlib frame (sent because IDENTIFY has <c>compress: true</c>).</summary>
    internal static byte[] Inflate(ReadOnlyMemory<byte> data)
    {
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private async Task<Outcome?> HandleAsync(WebSocketFrame frame, Connection connection, bool resume)
    {
        using var doc = JsonDocument.Parse(frame.IsText ? frame.Data : Inflate(frame.Data));
        var root = doc.RootElement;
        var op = root.GetProperty("op").GetInt32();
        if (root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number)
        {
            Interlocked.Exchange(ref _sequence, s.GetInt64());
        }

        var data = root.TryGetProperty("d", out var d) ? d : Null;
        var ct = connection.Cts.Token;
        switch (op)
        {
            case GatewayOp.Hello:
                if (!connection.Started)
                {
                    connection.Started = true;
                    var interval = TimeSpan.FromMilliseconds(data.GetProperty("heartbeat_interval").GetDouble());
                    connection.Heartbeat = HeartbeatLoopAsync(connection, interval);
                    await (resume ? SendResumeAsync(connection.Socket, ct) : SendIdentifyAsync(connection.Socket, ct))
                        .ConfigureAwait(false);
                }

                return null;

            case GatewayOp.Heartbeat:
                await SendHeartbeatAsync(connection.Socket, ct).ConfigureAwait(false);
                return null;

            case GatewayOp.HeartbeatAck:
                connection.Acked = true;
                return null;

            case GatewayOp.Reconnect:
                Log("Gateway requested a reconnect.");
                return Outcome.Resume;

            case GatewayOp.InvalidSession:
                var resumable = data.ValueKind == JsonValueKind.True;
                Log($"Gateway session invalidated (resumable: {resumable}).");
                return resumable ? Outcome.Resume : Outcome.Reidentify;

            case GatewayOp.Dispatch:
                var name = root.TryGetProperty("t", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                await OnDispatchAsync(name, data, ct).ConfigureAwait(false);
                return null;

            default:
                Log($"Unknown gateway op {op}.");
                return null;
        }
    }

    private async Task OnDispatchAsync(string name, JsonElement data, CancellationToken ct)
    {
        var ready = false;
        switch (name)
        {
            case "READY":
                _sessionId = data.GetProperty("session_id").GetString();
                _resumeUrl = data.TryGetProperty("resume_gateway_url", out var url) && url.GetString() is { } u
                    ? new Uri(u)
                    : null;
                Interlocked.Exchange(ref _userId, (long)(VoiceStateCache.Snowflake(data.GetProperty("user"), "id") ?? 0));
                Interlocked.Exchange(ref _healthy, 1);
                Log($"Gateway READY (session {_sessionId}).");
                ready = true;
                break;

            case "RESUMED":
                Interlocked.Exchange(ref _healthy, 1);
                Log($"Gateway RESUMED session {_sessionId}.");
                break;
        }

        var subscribe = SubscriptionTargets(name, data);

        VoiceStates.Apply(name, data);
        var copy = data.Clone();
        foreach (var handler in Dispatch?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<string, JsonElement>)handler)(name, copy);
            }
            catch (Exception ex)
            {
                Log($"Dispatch handler for {name} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (ready)
        {
            _ready.TrySetResult();

            // discord.py-self sends an initial op 4 (no guild, no channel) right after READY.
            await UpdateVoiceStateAsync(null, null, false, false, false, ct).ConfigureAwait(false);
        }

        if (subscribe.Count > 0)
        {
            await SubscribeAsync(subscribe, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Guilds to subscribe to after <paramref name="name"/> (spec 024): every guild in READY (a new session), or a guild
    /// arriving with GUILD_CREATE that this session hasn't subscribed to yet.
    /// </summary>
    private List<ulong> SubscriptionTargets(string name, JsonElement data)
    {
        if (!_options.SubscribeToGuilds || _subscriptionsRejected)
        {
            return [];
        }

        lock (_subscribed)
        {
            if (name == "READY")
            {
                _subscribed.Clear();
                return data.TryGetProperty("guilds", out var guilds) && guilds.ValueKind == JsonValueKind.Array
                    ? [.. guilds.EnumerateArray().Select(g => VoiceStateCache.Snowflake(g, "id")).OfType<ulong>().Distinct().Where(_subscribed.Add)]
                    : [];
            }

            return name == "GUILD_CREATE" && VoiceStateCache.Snowflake(data, "id") is { } id && _subscribed.Add(id) ? [id] : [];
        }
    }

    /// <summary>Op 37 GUILD_SUBSCRIPTIONS_BULK for <paramref name="guilds"/>, in batches; failures are logged, never thrown.</summary>
    private async Task SubscribeAsync(List<ulong> guilds, CancellationToken ct)
    {
        try
        {
            foreach (var batch in guilds.Chunk(Math.Max(1, _options.SubscriptionBatch)))
            {
                var subscriptions = new JsonObject();
                foreach (var guild in batch)
                {
                    subscriptions[guild.ToString(CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["typing"] = true,
                        ["threads"] = true,
                        ["activities"] = true,
                        ["member_updates"] = false,
                        ["members"] = new JsonArray(),
                        ["channels"] = new JsonObject(),
                        ["thread_member_lists"] = new JsonArray(),
                    };
                }

                _subscribedThisConnection = true;
                await SendAsync(GatewayOp.GuildSubscriptionsBulk, new JsonObject { ["subscriptions"] = subscriptions }, ct).ConfigureAwait(false);
            }

            Log($"Subscribed to {guilds.Count} guild(s) for messages.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log($"Guild subscription failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HeartbeatLoopAsync(Connection connection, TimeSpan interval)
    {
        var ct = connection.Cts.Token;
        try
        {
            await Task.Delay(interval * _options.Jitter(), _time, ct).ConfigureAwait(false);
            while (true)
            {
                if (!connection.Acked)
                {
                    Log("Gateway stopped acknowledging heartbeats; reconnecting.");
                    connection.Zombie = true;
                    await connection.Cts.CancelAsync().ConfigureAwait(false);
                    return;
                }

                connection.Acked = false;
                await SendHeartbeatAsync(connection.Socket, ct).ConfigureAwait(false);
                await Task.Delay(interval, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Connection ended.
        }
        catch (Exception ex)
        {
            Log($"Heartbeat failed: {ex.GetType().Name}; reconnecting.");
            connection.Zombie = true;
            await connection.Cts.CancelAsync().ConfigureAwait(false);
        }
    }

    private Task SendHeartbeatAsync(IWebSocketConnection socket, CancellationToken ct) =>
        SendRawAsync(socket, new JsonObject { ["op"] = GatewayOp.Heartbeat, ["d"] = Sequence }, ct);

    private async Task SendIdentifyAsync(IWebSocketConnection socket, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["op"] = GatewayOp.Identify,
            ["d"] = new JsonObject
            {
                ["token"] = _token,
                ["capabilities"] = _options.Capabilities,
                ["properties"] = _options.Properties.ToJson(),
                ["presence"] = new JsonObject
                {
                    ["status"] = "unknown",
                    ["since"] = 0,
                    ["activities"] = new JsonArray(),
                    ["afk"] = false,
                },
                // Discord requires at least one form of compression for user accounts
                // (discord.py-self: "We require at least one form of compression"). We use
                // per-payload zlib: large dispatches arrive as binary zlib frames (Inflate).
                ["compress"] = true,
                ["client_state"] = new JsonObject { ["guild_versions"] = new JsonObject() },
            },
        };
        await _limiter.WaitAsync(ct).ConfigureAwait(false);
        await SendRawAsync(socket, payload, ct).ConfigureAwait(false);
        Log("Sent IDENTIFY.");
    }

    private async Task SendResumeAsync(IWebSocketConnection socket, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["op"] = GatewayOp.Resume,
            ["d"] = new JsonObject
            {
                ["seq"] = Sequence,
                ["session_id"] = _sessionId,
                ["token"] = _token,
            },
        };
        await _limiter.WaitAsync(ct).ConfigureAwait(false);
        await SendRawAsync(socket, payload, ct).ConfigureAwait(false);
        Log("Sent RESUME.");
    }

    private async Task SendRawAsync(IWebSocketConnection? socket, JsonObject payload, CancellationToken ct)
    {
        if (socket is null)
        {
            throw new InvalidOperationException("The gateway is not connected.");
        }

        var json = payload.ToJsonString();
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendTextAsync(json, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void ResetSession()
    {
        _sessionId = null;
        _resumeUrl = null;
        Interlocked.Exchange(ref _sequence, -1);
    }

    private static async Task CloseQuietlyAsync(IWebSocketConnection socket, int code)
    {
        try
        {
            await socket.CloseAsync(code, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the connection is being abandoned either way.
        }
    }

    internal static Uri BuildUrl(Uri gateway) =>
        new UriBuilder(gateway) { Query = $"encoding=json&v={ApiVersion}" }.Uri;

    private void Log(string message) => _options.Log?.Invoke(message);
}

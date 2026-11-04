using System.Text.Json;
using System.Text.Json.Nodes;
using Slopsoil.Discord.Gateway;

namespace Slopsoil.Tests.Discord;

public sealed class GatewayClientTests : IAsyncLifetime
{
    private const string Token = "mfa.SECRET-TOKEN-VALUE";
    private const string ResumeUrl = "wss://gateway-us-east1-b.discord.gg";

    private const string Ready = """
        {"session_id":"sess-1","resume_gateway_url":"wss://gateway-us-east1-b.discord.gg",
         "user":{"id":"1000"},
         "guilds":[{"id":"1","voice_states":[{"user_id":"2","channel_id":"10"},{"user_id":"3","channel_id":null}]},
                   {"id":"5","unavailable":true}]}
        """;

    private readonly ManualTimeProvider _time = new();
    private readonly FakeSocketFactory _factory = new();
    private readonly List<string> _log = [];
    private GatewayClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _client = Create();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        lock (_log)
        {
            Assert.DoesNotContain(_log, line => line.Contains(Token, StringComparison.Ordinal));
        }
    }

    private GatewayClient Create(int rateLimit = 110) => new(_factory, new GatewayOptions
    {
        Time = _time,
        Jitter = () => 0.5,
        RateLimitCount = rateLimit,
        Log = line =>
        {
            lock (_log)
            {
                _log.Add(line);
            }
        },
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Connect, IDENTIFY, READY (seq 1), initial op 4 — returns the live socket.</summary>
    private async Task<FakeSocket> LoginAsync()
    {
        var connect = _client.ConnectAsync(Token, Ct);
        var ws = await _factory.NextAsync();
        ws.Hello();
        await ws.ExpectAsync(GatewayOp.Identify);
        ws.Dispatch("READY", Ready, 1);
        await connect.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await ws.ExpectAsync(GatewayOp.VoiceStateUpdate);
        return ws;
    }

    /// <summary>Waits until the client has processed everything pushed to <paramref name="ws"/> so far.</summary>
    private async Task SyncAsync(FakeSocket ws)
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string name, JsonElement _)
        {
            if (name == "SYNC")
            {
                seen.TrySetResult();
            }
        }

        _client.Dispatch += Handler;
        ws.Dispatch("SYNC", "{}", null);
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        _client.Dispatch -= Handler;
    }

    [Fact]
    public async Task Identify_payload_matches_discord_py_self()
    {
        var connect = _client.ConnectAsync(Token, Ct);
        var ws = await _factory.NextAsync();
        Assert.Equal("wss://gateway.discord.gg/?encoding=json&v=9", ws.Uri!.ToString());
        Assert.False(ws.HasSent);
        ws.Hello();

        var d = (await ws.ExpectAsync(GatewayOp.Identify)).GetProperty("d");
        Assert.Equal(
            ["token", "capabilities", "properties", "presence", "compress", "client_state"],
            d.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Token, d.GetProperty("token").GetString());
        Assert.Equal(22525, d.GetProperty("capabilities").GetInt32());
        Assert.True(d.GetProperty("compress").GetBoolean());
        Assert.Equal("""{"guild_versions":{}}""", d.GetProperty("client_state").GetRawText());
        Assert.Equal("""{"status":"unknown","since":0,"activities":[],"afk":false}""", d.GetProperty("presence").GetRawText());

        var props = d.GetProperty("properties");
        Assert.Equal(
            [
                "os", "browser", "device", "system_locale", "browser_user_agent", "browser_version", "os_version",
                "referrer", "referring_domain", "referrer_current", "referring_domain_current", "release_channel",
                "client_build_number", "client_event_source", "has_client_mods", "client_launch_id",
                "client_app_state", "client_heartbeat_session_id", "is_fast_connect", "latest_headless_tasks",
                "latest_headless_task_run_seconds_before", "gateway_connect_reasons",
            ],
            props.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Windows", props.GetProperty("os").GetString());
        Assert.Equal("Chrome", props.GetProperty("browser").GetString());
        Assert.Equal("136.0.0.0", props.GetProperty("browser_version").GetString());
        Assert.Equal(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36",
            props.GetProperty("browser_user_agent").GetString());
        Assert.Equal(9999, props.GetProperty("client_build_number").GetInt32());
        Assert.Equal(JsonValueKind.Null, props.GetProperty("client_event_source").ValueKind);
        Assert.True(Guid.TryParse(props.GetProperty("client_launch_id").GetString(), out _));
        Assert.Equal("AppSkeleton", props.GetProperty("gateway_connect_reasons").GetString());

        ws.Dispatch("READY", Ready, 1);
        await connect.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(1000UL, _client.UserId);
        Assert.Equal("sess-1", _client.SessionId);
        Assert.Equal(new Uri(ResumeUrl), _client.ResumeUrl);
        Assert.Equal(1, _client.Sequence);

        var op4 = await ws.ExpectAsync(GatewayOp.VoiceStateUpdate);
        Assert.Equal(
            """{"guild_id":null,"channel_id":null,"self_mute":false,"self_deaf":false,"self_video":false}""",
            op4.GetProperty("d").GetRawText());
    }

    [Fact]
    public async Task Heartbeat_jitters_first_beat_then_beats_every_interval_with_sequence()
    {
        var connect = _client.ConnectAsync(Token, Ct);
        var ws = await _factory.NextAsync();
        ws.Hello(40000);
        var first = await _time.NextTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(20), first.DueTime);
        await ws.ExpectAsync(GatewayOp.Identify);
        ws.Dispatch("READY", Ready, 7);
        await connect.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await ws.ExpectAsync(GatewayOp.VoiceStateUpdate);

        _time.Advance(TimeSpan.FromSeconds(20));
        var beat = await ws.ExpectAsync(GatewayOp.Heartbeat);
        Assert.Equal(7, beat.GetProperty("d").GetInt64());

        var next = await _time.NextTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(40), next.DueTime);
        ws.Op(GatewayOp.HeartbeatAck);
        ws.Dispatch("MESSAGE_CREATE", "{}", 8);
        await SyncAsync(ws);
        _time.Advance(TimeSpan.FromSeconds(40));
        beat = await ws.ExpectAsync(GatewayOp.Heartbeat);
        Assert.Equal(8, beat.GetProperty("d").GetInt64());

        // Server-requested heartbeat is answered immediately.
        ws.Op(GatewayOp.Heartbeat);
        await ws.ExpectAsync(GatewayOp.Heartbeat);
        Assert.Equal(1, _factory.Count);
    }

    [Fact]
    public async Task Heartbeat_before_any_dispatch_sends_null_sequence()
    {
        _ = _client.ConnectAsync(Token, Ct);
        var ws = await _factory.NextAsync();
        ws.Hello();
        await ws.ExpectAsync(GatewayOp.Identify);
        await _time.NextTimerAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        var beat = await ws.ExpectAsync(GatewayOp.Heartbeat);
        Assert.Equal(JsonValueKind.Null, beat.GetProperty("d").ValueKind);
    }

    [Fact]
    public async Task Missing_ack_is_a_zombie_connection_and_resumes()
    {
        var ws = await LoginAsync();
        await _time.NextTimerAsync();
        _time.Advance(TimeSpan.FromSeconds(30));
        await ws.ExpectAsync(GatewayOp.Heartbeat);
        await _time.NextTimerAsync();
        _time.Advance(TimeSpan.FromSeconds(42)); // no ACK arrived

        var ws2 = await _factory.NextAsync();
        // The old socket's close/dispose may still be finishing on the receive task when the new one is created.
        await VoiceConnectionTests.WaitUntil(() => ws.Disposed);
        Assert.Equal(4000, ws.ClosedWith);
        Assert.Equal("wss://gateway-us-east1-b.discord.gg/?encoding=json&v=9", ws2.Uri!.ToString());
        ws2.Hello();
        var resume = await ws2.ExpectAsync(GatewayOp.Resume);
        Assert.Equal("""{"seq":1,"session_id":"sess-1","token":"mfa.SECRET-TOKEN-VALUE"}""", resume.GetProperty("d").GetRawText());
        ws2.Dispatch("RESUMED", "{}", 2);
        await SyncAsync(ws2);
        Assert.Equal(2, _client.Sequence);
        Assert.Contains(_log, l => l.Contains("RESUMED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_heartbeat_send_reconnects_and_resumes()
    {
        var ws = await LoginAsync();
        await _time.NextTimerAsync();
        ws.FailSends = true;
        _time.Advance(TimeSpan.FromSeconds(30));
        var ws2 = await _factory.NextAsync();
        ws2.Hello();
        await ws2.ExpectAsync(GatewayOp.Resume);
    }

    [Fact]
    public async Task Invalid_session_not_resumable_reidentifies_after_a_wait()
    {
        var ws = await LoginAsync();
        ws.Op(GatewayOp.InvalidSession, "false");
        var wait = await NextNonHeartbeatTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(3), wait.DueTime); // 1 s + 4 s × jitter 0.5
        Assert.Equal(1000, ws.ClosedWith);
        Assert.Equal(string.Empty, _client.SessionId);
        Assert.Null(_client.Sequence);
        _time.Advance(TimeSpan.FromSeconds(3));

        var ws2 = await _factory.NextAsync();
        Assert.Equal("wss://gateway.discord.gg/?encoding=json&v=9", ws2.Uri!.ToString());
        ws2.Hello();
        await ws2.ExpectAsync(GatewayOp.Identify);
        ws2.Dispatch("READY", Ready.Replace("sess-1", "sess-2", StringComparison.Ordinal), 1);
        await ws2.ExpectAsync(GatewayOp.VoiceStateUpdate);
        Assert.Equal("sess-2", _client.SessionId);
    }

    [Fact]
    public async Task Invalid_session_resumable_resumes()
    {
        var ws = await LoginAsync();
        ws.Op(GatewayOp.InvalidSession, "true");
        var ws2 = await _factory.NextAsync();
        Assert.Equal(4000, ws.ClosedWith);
        ws2.Hello();
        await ws2.ExpectAsync(GatewayOp.Resume);
    }

    [Fact]
    public async Task Reconnect_op_resumes_on_the_resume_url()
    {
        var ws = await LoginAsync();
        ws.Op(GatewayOp.Reconnect);
        var ws2 = await _factory.NextAsync();
        Assert.Equal(4000, ws.ClosedWith);
        Assert.Equal("gateway-us-east1-b.discord.gg", ws2.Uri!.Host);
        ws2.Hello();
        var resume = await ws2.ExpectAsync(GatewayOp.Resume);
        Assert.Equal("sess-1", resume.GetProperty("d").GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task Dispatch_surfaces_message_create_and_tracks_sequence()
    {
        var ws = await LoginAsync();
        var messages = new List<string>();
        _client.Dispatch += (_, _) => throw new InvalidOperationException("bad handler");
        _client.Dispatch += (name, d) =>
        {
            if (name == "MESSAGE_CREATE")
            {
                messages.Add(d.GetProperty("content").GetString()!);
            }
        };

        ws.Dispatch("MESSAGE_CREATE", """{"content":"!join","channel_id":"10"}""", 42);
        ws.Op(99);
        await SyncAsync(ws);
        Assert.Equal(["!join"], messages);
        Assert.Equal(42, _client.Sequence);
        Assert.Contains(_log, l => l.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.Contains("op 99", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Binary_frames_are_zlib_inflated()
    {
        var ws = await LoginAsync();
        var messages = new List<string>();
        _client.Dispatch += (name, d) =>
        {
            if (name == "MESSAGE_CREATE")
            {
                messages.Add(d.GetProperty("content").GetString()!);
            }
        };

        ws.PushZlib("""{"op":0,"t":"MESSAGE_CREATE","d":{"content":"!media play"},"s":43}""");
        await SyncAsync(ws);
        Assert.Equal(["!media play"], messages);
        Assert.Equal(43, _client.Sequence);
    }

    [Fact]
    public async Task Voice_state_cache_follows_ready_guild_create_and_voice_state_update()
    {
        var ws = await LoginAsync();
        Assert.Equal(10UL, _client.GetVoiceChannel(1, 2));
        Assert.Null(_client.GetVoiceChannel(1, 3));

        ws.Dispatch("READY_SUPPLEMENTAL", """{"guilds":[{"id":"1","voice_states":[{"user_id":"4","channel_id":"11"}]}]}""", 2);
        ws.Dispatch("GUILD_CREATE", """{"id":"5","voice_states":[{"user_id":"1000","channel_id":"50"}]}""", 3);
        await SyncAsync(ws);
        Assert.Equal(11UL, _client.GetVoiceChannel(1, 4));
        Assert.Equal(10UL, _client.GetVoiceChannel(1, 2));
        Assert.Equal(50UL, _client.GetVoiceChannel(5, 1000));

        ws.Dispatch("VOICE_STATE_UPDATE", """{"guild_id":"1","user_id":"2","channel_id":"12"}""", 4);
        ws.Dispatch("VOICE_STATE_UPDATE", """{"guild_id":"1","user_id":"4","channel_id":null}""", 5);
        ws.Dispatch("VOICE_STATE_UPDATE", """{"user_id":"4","channel_id":"99"}""", 6); // DM call, no guild
        await SyncAsync(ws);
        Assert.Equal(12UL, _client.GetVoiceChannel(1, 2));
        Assert.Null(_client.GetVoiceChannel(1, 4));

        // GUILD_CREATE replaces the guild's states wholesale.
        ws.Dispatch("GUILD_CREATE", """{"id":"1","voice_states":[{"user_id":"7","channel_id":"13"}]}""", 7);
        await SyncAsync(ws);
        Assert.Null(_client.GetVoiceChannel(1, 2));
        Assert.Equal(new Dictionary<ulong, ulong> { [7] = 13 }, _client.VoiceStates.GetGuild(1));
    }

    [Fact]
    public async Task Rate_limit_delays_payloads_beyond_the_window_but_not_heartbeats()
    {
        await _client.DisposeAsync();
        _client = Create(rateLimit: 4);
        var ws = await LoginAsync(); // IDENTIFY + op 4 used two slots
        var heartbeat = await _time.NextTimerAsync();

        await _client.SendAsync(GatewayOp.PresenceUpdate, new { status = "online" }, Ct);
        await _client.SendAsync(GatewayOp.PresenceUpdate, null, Ct);
        await ws.ExpectAsync(GatewayOp.PresenceUpdate);
        await ws.ExpectAsync(GatewayOp.PresenceUpdate);

        var blocked = _client.SendAsync(GatewayOp.PresenceUpdate, new JsonObject { ["status"] = "idle" }, Ct);
        var wait = await _time.NextTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), wait.DueTime);
        Assert.False(blocked.IsCompleted);

        _time.Advance(heartbeat.DueTime);
        await ws.ExpectAsync(GatewayOp.Heartbeat); // bypasses the limiter
        Assert.False(blocked.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(60) - heartbeat.DueTime);
        await blocked.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var sent = await ws.ExpectAsync(GatewayOp.PresenceUpdate);
        Assert.Equal("idle", sent.GetProperty("d").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Update_voice_state_sends_op_4_with_snowflake_strings()
    {
        var ws = await LoginAsync();
        await _client.UpdateVoiceStateAsync(1, 10, selfMute: false, selfDeaf: true, selfVideo: false, Ct);
        var sent = await ws.ExpectAsync(GatewayOp.VoiceStateUpdate);
        Assert.Equal(
            """{"guild_id":"1","channel_id":"10","self_mute":false,"self_deaf":true,"self_video":false}""",
            sent.GetProperty("d").GetRawText());

        var node = new JsonObject { ["inner"] = new JsonObject { ["a"] = 1 } };
        await _client.SendAsync(GatewayOp.StreamCreate, node["inner"], Ct);
        Assert.Equal("""{"a":1}""", (await ws.ExpectAsync(GatewayOp.StreamCreate)).GetProperty("d").GetRawText());
    }

    [Fact]
    public async Task Fatal_close_code_fails_connect_and_completion()
    {
        var connect = _client.ConnectAsync(Token, Ct);
        var ws = await _factory.NextAsync();
        ws.PeerClose(4004);
        var ex = await Assert.ThrowsAsync<GatewayClosedException>(() => connect);
        Assert.Equal(4004, ex.Code);
        await Assert.ThrowsAsync<GatewayClosedException>(() => _client.Completion);
    }

    [Fact]
    public async Task Abnormal_close_backs_off_exponentially_and_resumes()
    {
        var ws = await LoginAsync();
        ws.PeerClose(1006);
        var wait = await NextNonHeartbeatTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), wait.DueTime);

        _factory.ConnectErrors.Enqueue(new IOException("dns"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await _factory.NextAsync(); // fails to connect
        wait = await _time.NextTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(2), wait.DueTime);

        _time.Advance(TimeSpan.FromSeconds(2));
        var ws3 = await _factory.NextAsync();
        ws3.Break();
        wait = await _time.NextTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(4), wait.DueTime);

        _time.Advance(TimeSpan.FromSeconds(4));
        var ws4 = await _factory.NextAsync();
        Assert.Equal("gateway-us-east1-b.discord.gg", ws4.Uri!.Host);
        ws4.Hello();
        await ws4.ExpectAsync(GatewayOp.Resume);
        ws4.Dispatch("RESUMED", "{}", 2);
        await SyncAsync(ws4);

        // Healthy again: backoff starts over.
        ws4.PeerClose(null);
        wait = await NextNonHeartbeatTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), wait.DueTime);
    }

    [Fact]
    public async Task Session_timeout_close_reidentifies()
    {
        var ws = await LoginAsync();
        ws.PeerClose(4009);
        var wait = await NextNonHeartbeatTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(3), wait.DueTime);
        _time.Advance(wait.DueTime);
        var ws2 = await _factory.NextAsync();
        Assert.Equal("gateway.discord.gg", ws2.Uri!.Host);
        ws2.Hello();
        await ws2.ExpectAsync(GatewayOp.Identify);
    }

    [Fact]
    public async Task Dispose_closes_with_1000_and_stops()
    {
        var ws = await LoginAsync();
        await _client.DisposeAsync();
        Assert.Equal(1000, ws.ClosedWith);
        await _client.Completion;
        Assert.Equal(1, _factory.Count);
    }

    [Fact]
    public async Task Connect_cancellation_stops_the_client()
    {
        using var cts = new CancellationTokenSource();
        var connect = _client.ConnectAsync(Token, cts.Token);
        await _factory.NextAsync();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        await _client.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Misuse_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.SendAsync(GatewayOp.PresenceUpdate, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _client.ConnectAsync(" ", Ct));
        await LoginAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.ConnectAsync(Token, Ct));
    }

    [Fact]
    public void Client_properties_expose_websocket_headers()
    {
        var props = new ClientProperties { BrowserMajorVersion = 140, ClientBuildNumber = 12345 };
        Assert.Contains("Chrome/140.0.0.0", props.WebSocketHeaders["User-Agent"], StringComparison.Ordinal);
        Assert.Equal("https://discord.com", props.WebSocketHeaders["Origin"]);
        Assert.Equal(12345, props.ToJson()["client_build_number"]!.GetValue<int>());
    }

    /// <summary>The next timer that is not the heartbeat loop's (the first heartbeat is due in 20.625 s).</summary>
    private async Task<ManualTimeProvider.ManualTimer> NextNonHeartbeatTimerAsync()
    {
        while (true)
        {
            var timer = await _time.NextTimerAsync();
            if (timer.DueTime != TimeSpan.FromMilliseconds(20625) && timer.DueTime != TimeSpan.FromMilliseconds(41250))
            {
                return timer;
            }
        }
    }
}

public class VoiceStateCacheTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Passive_update_and_guild_delete()
    {
        var cache = new VoiceStateCache();
        cache.Apply("GUILD_CREATE", Json("""{"id":"1","voice_states":[{"user_id":"2","channel_id":"10"},{"user_id":"3","channel_id":"10"}]}"""));
        cache.Apply("PASSIVE_UPDATE_V2", Json(
            """{"guild_id":"1","removed_voice_states":["2", 77],"updated_voice_states":[{"user_id":"4","channel_id":"11"}]}"""));
        Assert.Null(cache.GetVoiceChannel(1, 2));
        Assert.Equal(10UL, cache.GetVoiceChannel(1, 3));
        Assert.Equal(11UL, cache.GetVoiceChannel(1, 4));

        cache.Apply("GUILD_DELETE", Json("""{"id":"1"}"""));
        Assert.Empty(cache.GetGuild(1));
    }

    [Fact]
    public void Find_user_returns_the_lowest_guild_with_the_user_in_voice()
    {
        var cache = new VoiceStateCache();
        cache.Apply("GUILD_CREATE", Json("""{"id":"9","voice_states":[{"user_id":"2","channel_id":"90"}]}"""));
        cache.Apply("GUILD_CREATE", Json("""{"id":"3","voice_states":[{"user_id":"2","channel_id":"30"}]}"""));
        Assert.Equal((3UL, 30UL), cache.FindUser(2));
        Assert.Null(cache.FindUser(7));
    }

    [Fact]
    public void Ignores_malformed_and_unrelated_payloads()
    {
        var cache = new VoiceStateCache();
        cache.Apply("VOICE_STATE_UPDATE", Json("null"));
        cache.Apply("VOICE_STATE_UPDATE", Json("""{"guild_id":"x","user_id":"2","channel_id":"10"}"""));
        cache.Apply("VOICE_STATE_UPDATE", Json("""{"guild_id":1,"channel_id":"10"}"""));
        cache.Apply("GUILD_CREATE", Json("""{"voice_states":[]}"""));
        cache.Apply("GUILD_DELETE", Json("""{}"""));
        cache.Apply("PASSIVE_UPDATE_V2", Json("""{}"""));
        cache.Apply("READY_SUPPLEMENTAL", Json("""{"guilds":[{"voice_states":[]}, 5]}"""));
        cache.Apply("MESSAGE_CREATE", Json("""{"guild_id":"1"}"""));
        Assert.Empty(cache.GetGuild(1));

        cache.Apply("VOICE_STATE_UPDATE", Json("""{"guild_id":1,"user_id":2,"channel_id":10}"""));
        Assert.Equal(10UL, cache.GetVoiceChannel(1, 2));
        cache.Apply("READY", Json("""{"guilds":[]}"""));
        Assert.Null(cache.GetVoiceChannel(1, 2));
    }
}

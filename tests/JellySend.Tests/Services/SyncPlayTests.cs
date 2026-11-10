using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using JellySend.Services.Jellyfin;
using JellySend.Services.SyncPlay;
using JellySend.Tests.Discord;

namespace JellySend.Tests.Services;

public sealed class SyncPlayTests
{
    private const string Base = "http://jf.local:8096";

    private readonly FakeHttpHandler _http = new();
    private readonly ListLogger _log = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<SyncPlayCommand> _commands = [];
    private readonly List<SyncPlayQueue> _queues = [];
    private readonly Dictionary<string, HttpResponseMessage> _statuses = [];
    private FakeSyncPlaySocket _socket = new();

    public SyncPlayTests()
    {
        _http.Responder = r => r.Path == "/Users/AuthenticateByName"
            ? FakeHttpHandler.Json(new { AccessToken = "TOKEN", User = new { Id = "user-1" } })
            : _statuses.Remove(r.Path, out var response) ? response : new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IEnumerable<SeenRequest> Posts => _http.Requests.Where(r => r.Path.StartsWith("/SyncPlay/", StringComparison.Ordinal));

    private SyncPlayGroup Group(string url = Base, Func<SyncPlayCommand, Task>? onCommand = null)
    {
        var client = new JellyfinClient(url, username: "bob", password: "pw", http: _http.Client(), time: _time);
        return new SyncPlayGroup(
            client,
            SyncPlayGroup.DeviceIdFor(7),
            (c, _) =>
            {
                _commands.Add(c);
                return onCommand?.Invoke(c) ?? Task.CompletedTask;
            },
            (q, _) =>
            {
                _queues.Add(q);
                return Task.CompletedTask;
            },
            () => _socket,
            _log);
    }

    private async Task<SyncPlayGroup> OpenAsync(Func<SyncPlayCommand, Task>? onCommand = null)
    {
        var group = Group(onCommand: onCommand);
        await group.OpenAsync("g", Ct);
        _http.Requests.Clear();
        return group;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static Task Handle(SyncPlayGroup group, string json) => group.HandleMessageAsync(Parse(json), TestContext.Current.CancellationToken);

    // ---- naming / ticks ----

    [Fact]
    public void Device_id_and_group_name()
    {
        Assert.Equal("jellysend-123456789012345678", SyncPlayGroup.DeviceIdFor(123456789012345678));
        Assert.Equal("jellysend · Guild", SyncPlayGroup.GroupNameFor("Guild"));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1.5", 15_000_000L)]
    [InlineData("3600", 36_000_000_000L)]
    [InlineData("0.00000004", 0L)]
    [InlineData("0.00000025", 2L)] // half-to-even, like Python round()
    public void Seconds_to_ticks(string seconds, long ticks) =>
        Assert.Equal(ticks, JellyfinTicks.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("15000000", 1.5)]
    [InlineData("\"20000000\"", 2.0)]
    [InlineData("0", 0.0)]
    [InlineData("15000000.9", 1.5)] // int() truncates
    [InlineData("null", null)]
    [InlineData("\"abc\"", null)]
    [InlineData("{}", null)]
    public void Ticks_to_seconds(string json, double? seconds) => Assert.Equal(seconds, SyncPlayGroup.TicksToSeconds(Parse(json)));

    // ---- open / close ----

    [Theory]
    [InlineData("http://jf.local:8096", "ws://jf.local:8096")]
    [InlineData("https://jf.local:8096", "wss://jf.local:8096")]
    public async Task Open_signs_in_connects_and_creates_group(string url, string wsPrefix)
    {
        var group = Group(url);
        await group.OpenAsync("jellysend · Guild", Ct);

        var auth = _http.Requests[0];
        Assert.Equal("/Users/AuthenticateByName", auth.Path);
        Assert.Equal(AuthHeader.Build(null, "jellysend-7"), auth.Authorization);
        Assert.Equal(new Uri($"{wsPrefix}/socket?api_key=TOKEN&deviceId=jellysend-7"), _socket.Uri);
        var header = _socket.Headers!["Authorization"];
        Assert.Equal(AuthHeader.Build("TOKEN", "jellysend-7"), header);
        Assert.Equal([$"{url}/SyncPlay/New", $"{url}/SyncPlay/SetIgnoreWait"], Posts.Select(p => p.Url));
        Assert.Equal("jellysend · Guild", Posts.First().Json.GetProperty("GroupName").GetString());
        Assert.Equal("""{"IgnoreWait":true}""", Posts.Last().Body);
        Assert.All(Posts, p => Assert.Equal(header, p.Authorization));
        Assert.Equal("jellysend · Guild", group.GroupName);
        Assert.Equal("jellysend-7", group.DeviceId);
        Assert.NotNull(group.Reader);
        Assert.False(group.Closed);
        Assert.True(_log.Has(LogLevel.Information, "created group jellysend · Guild"));
        await group.CloseAsync(Ct);
    }

    [Fact]
    public async Task Open_new_group_http_error_raises_with_truncated_body()
    {
        _statuses["/SyncPlay/New"] = FakeHttpHandler.Json(string.Concat(Enumerable.Repeat("forbidden", 50)), HttpStatusCode.Forbidden);
        var group = Group();
        var ex = await Assert.ThrowsAsync<JellyfinException>(() => group.OpenAsync("g", Ct));
        Assert.StartsWith("SyncPlay /SyncPlay/New → HTTP 403: ", ex.Message, StringComparison.Ordinal);
        Assert.Equal(200, ex.Message.Split(": ", 2)[1].Length);
        Assert.Null(group.GroupName);
        await group.CloseAsync(Ct);
        Assert.True(group.Closed);
    }

    [Fact]
    public async Task Open_connect_failure_raises()
    {
        _socket.ConnectError = new IOException("refused");
        var group = Group();
        var ex = await Assert.ThrowsAsync<JellyfinException>(() => group.OpenAsync("g", Ct));
        Assert.DoesNotContain("TOKEN", ex.Message, StringComparison.Ordinal);
        Assert.Null(group.Reader);
    }

    [Fact]
    public async Task Open_sign_in_failure_raises()
    {
        _http.Responder = _ => FakeHttpHandler.Json("{}", HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<JellyfinException>(() => Group().OpenAsync("g", Ct));
        Assert.Null(_socket.Uri);
    }

    [Fact]
    public async Task Close_leaves_cancels_and_is_idempotent()
    {
        var group = await OpenAsync();
        await Handle(group, """{"MessageType": "ForceKeepAlive", "Data": 60}""");
        var keepAlive = group.KeepAlive!;

        await group.CloseAsync(Ct);

        Assert.True(group.Closed);
        var leave = Assert.Single(Posts);
        Assert.Equal(($"{Base}/SyncPlay/Leave", "{}"), (leave.Url, leave.Body));
        await group.Reader!.WaitAsync(Ct);
        await keepAlive.WaitAsync(Ct);
        Assert.Equal(1, _socket.CloseCalls);
        Assert.True(_socket.Disposed);
        Assert.True(_log.Has(LogLevel.Information, "left group g"));

        await group.CloseAsync(Ct); // second close is a no-op
        Assert.Equal(1, _socket.CloseCalls);
        Assert.Single(Posts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => group.PauseAsync(Ct));
    }

    [Fact]
    public async Task Close_tolerates_leave_and_socket_failures()
    {
        var group = await OpenAsync();
        _statuses["/SyncPlay/Leave"] = FakeHttpHandler.Json("boom", HttpStatusCode.InternalServerError);
        _socket.CloseError = new IOException("ws gone");
        await group.DisposeAsync();
        Assert.True(group.Closed);
        Assert.True(_socket.Disposed);
        Assert.True(_log.Has(LogLevel.Debug, "leave failed"));
    }

    [Fact]
    public async Task Close_before_open()
    {
        var group = Group();
        await group.CloseAsync(Ct); // Leave fails (not open) and is swallowed
        Assert.True(group.Closed);
        Assert.Empty(_http.Requests);
    }

    // ---- REST requests ----

    [Fact]
    public async Task Requests_require_an_open_group()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Group().PauseAsync(Ct));
        Assert.Contains("not open", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_bodies()
    {
        var group = await OpenAsync();
        await group.SetQueueAsync("item-9", 90.5, Ct);
        await group.SetQueueAsync("item-1", ct: Ct);
        await group.PauseAsync(Ct);
        await group.UnpauseAsync(Ct);
        await group.SeekAsync(12.25, Ct);
        Assert.Equal(
            [
                ("/SyncPlay/SetNewQueue", """{"PlayingQueue":["item-9"],"PlayingItemPosition":0,"StartPositionTicks":905000000}"""),
                ("/SyncPlay/SetNewQueue", """{"PlayingQueue":["item-1"],"PlayingItemPosition":0,"StartPositionTicks":0}"""),
                ("/SyncPlay/Pause", "{}"),
                ("/SyncPlay/Unpause", "{}"),
                ("/SyncPlay/Seek", """{"PositionTicks":122500000}"""),
            ],
            Posts.Select(p => (p.Path, p.Body)));
        await group.CloseAsync(Ct);
    }

    [Fact]
    public async Task Request_error_status_raises()
    {
        var group = await OpenAsync();
        _statuses["/SyncPlay/Seek"] = FakeHttpHandler.Json("bad ticks", HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<JellyfinException>(() => group.SeekAsync(1, Ct));
        Assert.Equal("SyncPlay /SyncPlay/Seek → HTTP 400: bad ticks", ex.Message);
        await group.CloseAsync(Ct);
    }

    [Fact]
    public async Task Request_network_failure_and_timeout_raise()
    {
        var group = await OpenAsync();
        _http.Responder = _ => throw new HttpRequestException("reset");
        Assert.Equal("SyncPlay /SyncPlay/Pause failed", (await Assert.ThrowsAsync<JellyfinException>(() => group.PauseAsync(Ct))).Message);

        _http.AsyncResponder = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };
        var pause = group.PauseAsync(Ct);
        await _time.NextTimerAsync();
        _time.Advance(SyncPlayGroup.RequestTimeout);
        Assert.Equal("SyncPlay /SyncPlay/Pause timed out", (await Assert.ThrowsAsync<JellyfinException>(() => pause)).Message);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => group.UnpauseAsync(cts.Token));
        _http.AsyncResponder = null;
        await group.CloseAsync(Ct);
    }

    // ---- keepalive ----

    [Theory]
    [InlineData("60", 30.0)]
    [InlineData("\"8\"", 5.0)]
    [InlineData("null", 30.0)]
    [InlineData("30", 15.0)]
    [InlineData("0", 30.0)]
    [InlineData("\"\"", 30.0)]
    [InlineData("true", 5.0)]
    [InlineData("false", 30.0)]
    public async Task Force_keepalive_sends_keepalive_every_half_interval(string data, double interval)
    {
        var group = await OpenAsync();
        await Handle(group, $$"""{"MessageType": "ForceKeepAlive", "Data": {{data}}}""");
        var first = group.KeepAlive;
        await Handle(group, $$"""{"MessageType": "ForceKeepAlive", "Data": {{data}}}""");
        Assert.Same(first, group.KeepAlive); // started once

        for (var i = 0; i < 3; i++)
        {
            var timer = await _time.NextTimerAsync();
            Assert.Equal(TimeSpan.FromSeconds(interval), timer.DueTime);
            Assert.False(_socket.HasSent);
            _time.Advance(timer.DueTime);
            Assert.Equal("""{"MessageType":"KeepAlive"}""", await _socket.NextSentAsync());
        }

        await group.CloseAsync(Ct);
        await first!.WaitAsync(Ct);
    }

    [Fact]
    public async Task Force_keepalive_without_data_uses_60s()
    {
        var group = await OpenAsync();
        await Handle(group, """{"MessageType": "ForceKeepAlive"}""");
        Assert.Equal(TimeSpan.FromSeconds(30), (await _time.NextTimerAsync()).DueTime);
        await group.CloseAsync(Ct);
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("{}")]
    public async Task Force_keepalive_with_bad_data_throws(string data)
    {
        var group = await OpenAsync();
        await Assert.ThrowsAsync<FormatException>(() => Handle(group, $$"""{"MessageType": "ForceKeepAlive", "Data": {{data}}}"""));
        Assert.Null(group.KeepAlive);
        await group.CloseAsync(Ct);
    }

    [Fact]
    public async Task Keepalive_stops_on_send_failure_and_restarts_on_next_request()
    {
        var group = await OpenAsync();
        _socket.SendError = new IOException("gone");
        await Handle(group, """{"MessageType": "ForceKeepAlive", "Data": 10}""");
        var first = group.KeepAlive!;
        _time.Advance((await _time.NextTimerAsync()).DueTime);
        await first.WaitAsync(Ct); // returns instead of looping forever
        Assert.True(_log.Has(LogLevel.Debug, "keepalive failed"));

        _socket.SendError = null;
        await Handle(group, """{"MessageType": "ForceKeepAlive", "Data": 10}""");
        Assert.NotSame(first, group.KeepAlive);
        _time.Advance((await _time.NextTimerAsync()).DueTime);
        Assert.Equal("""{"MessageType":"KeepAlive"}""", await _socket.NextSentAsync());
        await group.CloseAsync(Ct);
    }

    [Fact]
    public async Task Keepalive_without_socket_returns_immediately()
    {
        var group = Group();
        await Handle(group, """{"MessageType": "ForceKeepAlive", "Data": 10}""");
        Assert.True(group.KeepAlive!.IsCompleted);
    }

    // ---- handle_message ----

    [Fact]
    public async Task Sync_commands_are_forwarded_from_the_socket()
    {
        var group = await OpenAsync();
        _socket.Push(new { MessageType = "SyncPlayCommand", Data = new { Command = "Seek", PositionTicks = 600_000_000 } });
        _socket.Push(new { MessageType = "SyncPlayCommand", Data = new { Command = "Pause" } });
        _socket.PeerClose();
        await group.Reader!.WaitAsync(Ct);
        Assert.Equal([new SyncPlayCommand("Seek", 60.0), new SyncPlayCommand("Pause", null)], _commands);
    }

    [Theory]
    [InlineData("""{"MessageType": "SyncPlayCommand", "Data": {"PositionTicks": 1}}""")]
    [InlineData("""{"MessageType": "SyncPlayCommand", "Data": {"Command": ""}}""")]
    [InlineData("""{"MessageType": "SyncPlayCommand", "Data": "not a dict"}""")]
    [InlineData("""{"MessageType": "SyncPlayGroupUpdate", "Data": null}""")]
    [InlineData("""{"MessageType": "SyncPlayGroupUpdate", "Data": {"Type": "PlayQueue", "Data": []}}""")]
    [InlineData("""{"MessageType": "SyncPlayGroupUpdate", "Data": {"Type": "GroupJoined", "Data": "x"}}""")]
    [InlineData("""{"MessageType": "SyncPlayGroupUpdate", "Data": {"Type": "StateUpdate", "Data": {}}}""")]
    [InlineData("""{"MessageType": "SyncPlayGroupUpdate", "Data": {"Type": 5}}""")]
    [InlineData("""{"MessageType": "UserDataChanged", "Data": {}}""")]
    [InlineData("""{"MessageType": 1}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    public async Task Ignored_messages(string json)
    {
        var group = Group();
        await Handle(group, json);
        Assert.Empty(_commands);
        Assert.Empty(_queues);
        Assert.Null(group.GroupId);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task Group_joined_sets_group_id()
    {
        var group = Group();
        await Handle(group, """{"MessageType": "SyncPlayGroupUpdate", "Data": {"Type": "GroupJoined", "Data": {"GroupId": "g-42"}}}""");
        Assert.Equal("g-42", group.GroupId);
    }

    [Theory]
    [InlineData("""{"Playlist": [{"ItemId": "a"}, {"ItemId": "b"}], "PlayingItemIndex": 1, "StartPositionTicks": 300000000, "IsPlaying": true}""", "b", 30.0, true)]
    [InlineData("""{"Playlist": [{"ItemId": "a"}]}""", "a", 0.0, false)]
    [InlineData("""{"Playlist": [{"ItemId": "a"}], "PlayingItemIndex": 5}""", null, 0.0, false)]
    [InlineData("""{"Playlist": [{"ItemId": "a"}], "PlayingItemIndex": -1}""", null, 0.0, false)]
    [InlineData("""{"Playlist": [{"ItemId": "a"}], "PlayingItemIndex": "0"}""", null, 0.0, false)]
    [InlineData("""{"Playlist": [{"ItemId": "a"}], "PlayingItemIndex": 0.5}""", null, 0.0, false)]
    [InlineData("""{"Playlist": ["a"]}""", null, 0.0, false)]
    [InlineData("""{"Playlist": [{"ItemId": 1}]}""", null, 0.0, false)]
    [InlineData("""{"Playlist": null, "StartPositionTicks": "junk"}""", null, 0.0, false)]
    [InlineData("""{"StartPositionTicks": "50000000", "IsPlaying": 1}""", null, 5.0, false)]
    public async Task Play_queue_update(string payload, string? item, double start, bool playing)
    {
        var group = Group();
        await Handle(group, $$$"""{"MessageType": "SyncPlayGroupUpdate", "Data": {"Type": "PlayQueue", "Data": {{{payload}}} }}""");
        Assert.Equal(new SyncPlayQueue(item, start, playing), Assert.Single(_queues));
    }

    [Theory]
    [InlineData("GroupLeft", LogLevel.Information, "GroupLeft")]
    [InlineData("NotInGroup", LogLevel.Information, "NotInGroup")]
    [InlineData("GroupDoesNotExist", LogLevel.Information, "GroupDoesNotExist")]
    [InlineData("CreateGroupDenied", LogLevel.Warning, "SyncPlay permission")]
    [InlineData("JoinGroupDenied", LogLevel.Warning, "SyncPlay permission")]
    [InlineData("LibraryAccessDenied", LogLevel.Warning, "SyncPlay permission")]
    public async Task Group_status_updates_are_logged(string kind, LogLevel level, string text)
    {
        var group = Group();
        await Handle(group, $$$"""{"MessageType": "SyncPlayGroupUpdate", "Data": {"Type": "{{{kind}}}"} }""");
        Assert.True(_log.Has(level, text));
    }

    // ---- websocket reader ----

    [Fact]
    public async Task Reader_dispatches_text_skips_other_types_and_marks_dead_on_close()
    {
        var group = await OpenAsync();
        _socket.PushBinary();
        _socket.Push(new { MessageType = "SyncPlayCommand", Data = new { Command = "Unpause" } });
        _socket.Push("{not json");
        _socket.Push(new { MessageType = "SyncPlayCommand", Data = new { Command = "Pause" } });
        _socket.PeerClose();
        _socket.Push(new { MessageType = "SyncPlayCommand", Data = new { Command = "Seek" } }); // after close: never read

        await group.Reader!.WaitAsync(Ct);

        Assert.Equal(["Unpause", "Pause"], _commands.Select(c => c.Command));
        Assert.True(_log.Has(LogLevel.Error, "error handling message"));
        Assert.True(_log.Has(LogLevel.Warning, "websocket closed for group g"));
        Assert.True(group.Closed);
        Assert.True(_socket.Disposed);
        Assert.Equal(0, _socket.CloseCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => group.PauseAsync(Ct));
    }

    [Fact]
    public async Task Reader_handler_error_is_logged_and_loop_continues()
    {
        var calls = 0;
        var group = await OpenAsync(_ => ++calls == 1 ? throw new InvalidOperationException("cb") : Task.CompletedTask);
        _socket.Push(new { MessageType = "SyncPlayCommand", Data = new { Command = "Seek" } });
        _socket.Push(new { MessageType = "SyncPlayCommand", Data = new { Command = "Seek" } });
        _socket.PeerClose();
        await group.Reader!.WaitAsync(Ct);
        Assert.Equal(2, _commands.Count);
        Assert.True(_log.Has(LogLevel.Error, "error handling message"));
    }

    [Fact]
    public async Task Reader_error_marks_group_dead_and_stops_keepalive()
    {
        var group = await OpenAsync();
        await Handle(group, """{"MessageType": "ForceKeepAlive", "Data": 60}""");
        var keepAlive = group.KeepAlive!;
        _socket.Break(new IOException("reset"));
        await group.Reader!.WaitAsync(Ct);
        await keepAlive.WaitAsync(Ct);
        Assert.True(_log.Has(LogLevel.Warning, "websocket reader ended"));
        Assert.True(_log.Has(LogLevel.Warning, "websocket closed for group"));
        Assert.True(group.Closed);
        Assert.True(_socket.Disposed);
    }

    [Fact]
    public async Task Close_after_dead_socket_is_noop()
    {
        var group = await OpenAsync();
        _socket.PeerClose();
        await group.Reader!.WaitAsync(Ct);
        await group.CloseAsync(Ct); // already marked closed by the reader
        Assert.Empty(Posts);
        Assert.False(_log.Has(LogLevel.Information, "left group"));
    }

    [Fact]
    public async Task Reopen_uses_a_fresh_group_and_socket()
    {
        var group = await OpenAsync();
        _socket.PeerClose();
        await group.Reader!.WaitAsync(Ct);
        Assert.True(group.Closed);

        _socket = new FakeSyncPlaySocket();
        var next = await OpenAsync();
        Assert.False(next.Closed);
        Assert.NotNull(_socket.Uri);
        await next.CloseAsync(Ct);
    }
}

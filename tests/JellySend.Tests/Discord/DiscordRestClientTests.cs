using System.Net;
using System.Text;
using System.Text.Json;
using JellySend.Discord.Gateway;
using JellySend.Discord.Rest;

namespace JellySend.Tests.Discord;

public sealed class DiscordRestClientTests
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<(HttpRequestMessage Request, string? Body)> _requests = [];
    private readonly ManualTimeProvider _time = new();
    private readonly DiscordRestClient _client;

    public DiscordRestClientTests()
    {
        _client = new DiscordRestClient(new HttpClient(new Handler(this)), "user-token", new ClientProperties(), _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Respond(HttpStatusCode status, string? body = null) =>
        new(status) { Content = new StringContent(body ?? "", Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Send_message_posts_json_with_browser_headers()
    {
        _responses.Enqueue(Respond(HttpStatusCode.OK, "{}"));
        await _client.SendMessageAsync(123, "pong!", Ct);

        var (request, body) = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://discord.com/api/v9/channels/123/messages", request.RequestUri!.ToString());
        Assert.Equal("user-token", request.Headers.GetValues("Authorization").Single());
        Assert.Equal(new ClientProperties().UserAgent, request.Headers.UserAgent.ToString());
        var super = JsonDocument.Parse(Convert.FromBase64String(request.Headers.GetValues("X-Super-Properties").Single()));
        Assert.Equal("Chrome", super.RootElement.GetProperty("browser").GetString());
        Assert.All(ClientProperties.GatewayOnlyFields, f => Assert.False(super.RootElement.TryGetProperty(f, out _), f));
        Assert.True(super.RootElement.TryGetProperty("client_build_number", out _));
        Assert.Equal("en-US", request.Headers.GetValues("X-Discord-Locale").Single());

        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal(["mobile_network_type", "content", "nonce", "tts", "flags"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("pong!", json.GetProperty("content").GetString());
        Assert.Equal(_client.Nonce(), json.GetProperty("nonce").GetString());
    }

    [Fact]
    public async Task Voice_channels_come_in_discords_order_without_text_stage_or_afk_channels()
    {
        _responses.Enqueue(Respond(HttpStatusCode.OK, """{"id": "5", "afk_channel_id": "30"}"""));
        _responses.Enqueue(Respond(HttpStatusCode.OK, """
            [
              {"id": "10", "type": 0, "position": 0},
              {"id": "40", "type": 2, "position": 2, "name": "Movie Night"},
              {"id": "30", "type": 2, "position": 0},
              {"id": "21", "type": 2, "position": 1},
              {"id": "20", "type": 2, "position": 1},
              {"id": "50", "type": 13, "position": 0},
              {"id": "60", "type": 2}
            ]
            """));
        Assert.Equal([20UL, 21, 40, 60], (await _client.GetVoiceChannelsAsync(5, Ct)).Select(c => c.Id));
        Assert.Equal(
            ["https://discord.com/api/v9/guilds/5", "https://discord.com/api/v9/guilds/5/channels"],
            _requests.Select(r => r.Request.RequestUri!.ToString()));
        Assert.All(_requests, r => Assert.Equal(HttpMethod.Get, r.Request.Method));
    }

    [Fact]
    public async Task Scheduled_events_are_read_and_started()
    {
        _responses.Enqueue(Respond(HttpStatusCode.OK, """
            [
              {"id":"9","name":"Movie Night","description":"Screenings:\n1. A","channel_id":"300","status":1,"entity_type":2,
               "scheduled_start_time":"2026-11-06T09:30:00+00:00","creator_id":"20"},
              {"id":"10","name":"Elsewhere","description":null,"channel_id":null,"status":2,"entity_type":3}
            ]
            """));
        var events = await _client.GetScheduledEventsAsync(5, Ct);
        Assert.Equal(
            new ScheduledEvent(9, 5, "Movie Night", "Screenings:\n1. A", 300, 1, 2, new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.Zero), 20),
            events[0]);
        Assert.Equal(new ScheduledEvent(10, 5, "Elsewhere", "", null, 2, 3, DateTimeOffset.MaxValue, null), events[1]);

        _responses.Enqueue(Respond(HttpStatusCode.OK, "{}"));
        await _client.SetScheduledEventStatusAsync(5, 9, 2, Ct);
        var (request, body) = _requests[^1];
        Assert.Equal("PATCH", request.Method.Method);
        Assert.Equal("https://discord.com/api/v9/guilds/5/scheduled-events/9", request.RequestUri!.ToString());
        Assert.Equal("""{"status":2}""", body);
    }

    [Theory]
    [InlineData(true, "PUT")]
    [InlineData(false, "DELETE")]
    public async Task Event_interest_is_set_and_cleared(bool interested, string method)
    {
        _responses.Enqueue(Respond(HttpStatusCode.NoContent));
        await _client.SetEventInterestAsync(5, 9, interested, Ct);
        var (request, body) = Assert.Single(_requests);
        Assert.Equal(method, request.Method.Method);
        Assert.Equal("https://discord.com/api/v9/guilds/5/scheduled-events/9/users/@me", request.RequestUri!.ToString());
        Assert.Null(body);
    }

    [Fact]
    public async Task A_dm_channel_is_opened_with_the_user()
    {
        _responses.Enqueue(Respond(HttpStatusCode.OK, """{"id":"900","type":1}"""));
        Assert.Equal(900UL, await _client.OpenDmAsync(20, Ct));
        Assert.Equal("""{"recipients":["20"]}""", _requests[0].Body);
        Assert.Equal("https://discord.com/api/v9/users/@me/channels", _requests[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Guilds_come_with_their_names()
    {
        _responses.Enqueue(Respond(HttpStatusCode.OK, """[{"id":"5","name":"Movie Night"},{"id":"6"}]"""));
        Assert.Equal([(5UL, "Movie Night"), (6UL, "server")], await _client.GetGuildsAsync(Ct));
        Assert.Equal("https://discord.com/api/v9/users/@me/guilds", _requests[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Voice_channels_without_an_afk_channel()
    {
        _responses.Enqueue(Respond(HttpStatusCode.OK, """{"id": "5", "afk_channel_id": null}"""));
        _responses.Enqueue(Respond(HttpStatusCode.OK, """[{"id": "7", "type": 2, "position": 0}]"""));
        Assert.Equal([(7UL, "voice")], await _client.GetVoiceChannelsAsync(5, Ct));
    }

    [Fact]
    public void Nonce_is_a_time_snowflake()
    {
        var ms = _time.GetUtcNow().ToUnixTimeMilliseconds() - DiscordRestClient.DiscordEpochMs;
        Assert.Equal((ms << 22).ToString(System.Globalization.CultureInfo.InvariantCulture), _client.Nonce());
    }

    [Fact]
    public async Task Rate_limits_wait_retry_after_then_retry()
    {
        _responses.Enqueue(Respond(HttpStatusCode.TooManyRequests, """{"retry_after": 1.5}"""));
        var r = Respond(HttpStatusCode.TooManyRequests, "not json");
        r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
        _responses.Enqueue(r);
        _responses.Enqueue(Respond(HttpStatusCode.OK));

        var send = _client.SendMessageAsync(1, "x", Ct);
        await WaitForRequestsAsync(1);
        _time.Advance(TimeSpan.FromSeconds(1.4));
        Assert.Single(_requests);
        _time.Advance(TimeSpan.FromSeconds(0.1));
        await WaitForRequestsAsync(2);
        _time.Advance(TimeSpan.FromSeconds(2));
        await send;
        Assert.Equal(3, _requests.Count);
    }

    [Fact]
    public async Task Gives_up_after_max_retries_and_on_errors()
    {
        for (var i = 0; i <= DiscordRestClient.MaxRetries; i++)
        {
            _responses.Enqueue(Respond(HttpStatusCode.TooManyRequests, """{"retry_after": 0}"""));
        }

        var ex = await Assert.ThrowsAsync<DiscordRestException>(() => _client.SendMessageAsync(1, "x", Ct));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.Status);
        Assert.Equal(DiscordRestClient.MaxRetries + 1, _requests.Count);

        _responses.Enqueue(Respond(HttpStatusCode.Forbidden, """{"message":"secret content"}"""));
        var forbidden = await Assert.ThrowsAsync<DiscordRestException>(() => _client.SendMessageAsync(9, "x", Ct));
        Assert.Equal("Discord REST POST channels/9/messages failed with HTTP 403", forbidden.Message);
    }

    [Fact]
    public void Token_is_required() =>
        Assert.Throws<ArgumentException>(() => new DiscordRestClient(new HttpClient(), " "));

    private async Task WaitForRequestsAsync(int count)
    {
        for (var i = 0; i < 500 && _requests.Count < count; i++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.Equal(count, _requests.Count);
    }

    private sealed class Handler(DiscordRestClientTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (owner._requests)
            {
                owner._requests.Add((request, body));
            }

            return owner._responses.Dequeue();
        }
    }
}

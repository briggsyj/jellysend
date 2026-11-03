using System.Net;
using System.Text;
using System.Text.Json;
using Slopsoil.Discord.Gateway;
using Slopsoil.Discord.Rest;

namespace Slopsoil.Tests.Discord;

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
        Assert.Equal("en-US", request.Headers.GetValues("X-Discord-Locale").Single());

        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal(["mobile_network_type", "content", "nonce", "tts", "flags"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("pong!", json.GetProperty("content").GetString());
        Assert.Equal(_client.Nonce(), json.GetProperty("nonce").GetString());
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

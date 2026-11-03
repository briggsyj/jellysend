using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Slopsoil.Discord.Gateway;

namespace Slopsoil.Discord.Rest;

/// <summary>Raised for a non-success Discord REST response (the body is not included: it may echo content).</summary>
public sealed class DiscordRestException(HttpStatusCode status, string route)
    : Exception($"Discord REST {route} failed with HTTP {(int)status}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The REST calls the bot needs, sent with the same browser-like headers as <c>discord.py-self</c>
/// (<c>HTTPClient.request</c>): user token, user agent, base64 <c>X-Super-Properties</c>, locale.
/// 429s wait for <c>retry_after</c> and retry (at most <see cref="MaxRetries"/> times).
/// </summary>
public sealed class DiscordRestClient
{
    public const int MaxRetries = 3;

    /// <summary>Discord's epoch (2015-01-01) for nonce snowflakes.</summary>
    public const long DiscordEpochMs = 1_420_070_400_000;

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly ClientProperties _properties;
    private readonly TimeProvider _time;
    private readonly string _superProperties;

    public DiscordRestClient(HttpClient http, string token, ClientProperties? properties = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _http = http;
        _token = token;
        _properties = properties ?? new ClientProperties();
        _time = time ?? TimeProvider.System;
        _superProperties = Convert.ToBase64String(Encoding.UTF8.GetBytes(_properties.ToJson().ToJsonString()));
    }

    public static Uri ApiBase { get; } = new("https://discord.com/api/v9/");

    /// <summary>POST /channels/{id}/messages (<c>Messageable.send</c>).</summary>
    public Task SendMessageAsync(ulong channelId, string content, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["mobile_network_type"] = "unknown",
            ["content"] = content,
            ["nonce"] = Nonce(),
            ["tts"] = false,
            ["flags"] = 0,
        };
        return SendAsync(HttpMethod.Post, $"channels/{channelId.ToString(CultureInfo.InvariantCulture)}/messages", body, ct);
    }

    /// <summary>Time-based snowflake, as <c>utils._generate_nonce</c>.</summary>
    internal string Nonce() =>
        ((_time.GetUtcNow().ToUnixTimeMilliseconds() - DiscordEpochMs) << 22).ToString(CultureInfo.InvariantCulture);

    private async Task SendAsync(HttpMethod method, string route, JsonNode? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri(ApiBase, route));
            request.Headers.TryAddWithoutValidation("Authorization", _token);
            request.Headers.TryAddWithoutValidation("User-Agent", _properties.UserAgent);
            request.Headers.TryAddWithoutValidation("X-Super-Properties", _superProperties);
            request.Headers.TryAddWithoutValidation("X-Discord-Locale", _properties.SystemLocale);
            request.Headers.TryAddWithoutValidation("X-Debug-Options", "bugReporterEnabled");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Origin", "https://discord.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://discord.com/channels/@me");
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRetries)
            {
                var wait = await RetryAfterAsync(response, ct).ConfigureAwait(false);
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                continue;
            }

            throw new DiscordRestException(response.StatusCode, $"{method} {route}");
        }
    }

    /// <summary><c>retry_after</c> seconds from the JSON body, else the Retry-After header, else 1 s; capped at 60 s.</summary>
    private static async Task<TimeSpan> RetryAfterAsync(HttpResponseMessage response, CancellationToken ct)
    {
        double? seconds = null;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("retry_after", out var r) && r.TryGetDouble(out var v))
            {
                seconds = v;
            }
        }
        catch (JsonException)
        {
        }

        seconds ??= response.Headers.RetryAfter?.Delta?.TotalSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds ?? 1.0, 0.0, 60.0));
    }
}

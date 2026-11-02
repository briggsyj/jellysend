using System.Text.Json.Nodes;

namespace Slopsoil.Discord.Gateway;

/// <summary>
/// The client "super properties" sent in IDENTIFY — the same shape <c>discord.py-self</c> sends
/// (<c>utils.Headers.default</c> + <c>extra_gateway_properties</c>: Chrome on Windows 10, web client).
/// Values are fixed and documented here instead of fetched at startup: the browser version and build
/// number are <c>discord.py-self</c>'s own fallbacks (<c>FALLBACK_BROWSER_VERSION</c> /
/// <c>FALLBACK_BUILD_NUMBER</c>). Nothing beyond this is sent.
/// </summary>
public sealed record ClientProperties
{
    /// <summary><c>Headers.FALLBACK_BROWSER_VERSION</c>.</summary>
    public const int DefaultBrowserMajorVersion = 136;

    /// <summary><c>Headers.FALLBACK_BUILD_NUMBER</c>.</summary>
    public const int DefaultBuildNumber = 9999;

    public int BrowserMajorVersion { get; init; } = DefaultBrowserMajorVersion;

    public int ClientBuildNumber { get; init; } = DefaultBuildNumber;

    public string SystemLocale { get; init; } = "en-US";

    /// <summary>Random per process, like <c>discord.py-self</c> (<c>uuid4()</c> at startup).</summary>
    public string ClientLaunchId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Random per process, like <c>discord.py-self</c> (<c>uuid4()</c> at startup).</summary>
    public string ClientHeartbeatSessionId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Chrome's reduced user agent for <see cref="BrowserMajorVersion"/> (<c>Headers._get_user_agent</c>).</summary>
    public string UserAgent =>
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{BrowserMajorVersion}.0.0.0 Safari/537.36";

    /// <summary>
    /// Request headers for the gateway websocket (<c>HTTPClient.ws_connect</c>), minus
    /// <c>Accept-Encoding</c> / <c>Sec-WebSocket-Extensions</c> since no compression is negotiated.
    /// </summary>
    public IReadOnlyDictionary<string, string> WebSocketHeaders => new Dictionary<string, string>
    {
        ["Accept-Language"] = "en-US,en;q=0.9",
        ["Origin"] = "https://discord.com",
        ["User-Agent"] = UserAgent,
    };

    /// <summary>The IDENTIFY <c>properties</c> object (<c>Headers.gateway_properties</c>).</summary>
    public JsonObject ToJson() => new()
    {
        ["os"] = "Windows",
        ["browser"] = "Chrome",
        ["device"] = "",
        ["system_locale"] = SystemLocale,
        ["browser_user_agent"] = UserAgent,
        ["browser_version"] = $"{BrowserMajorVersion}.0.0.0",
        ["os_version"] = "10",
        ["referrer"] = "",
        ["referring_domain"] = "",
        ["referrer_current"] = "",
        ["referring_domain_current"] = "",
        ["release_channel"] = "stable",
        ["client_build_number"] = ClientBuildNumber,
        ["client_event_source"] = null,
        ["has_client_mods"] = false,
        ["client_launch_id"] = ClientLaunchId,
        ["client_app_state"] = "unfocused",
        ["client_heartbeat_session_id"] = ClientHeartbeatSessionId,
        ["is_fast_connect"] = false,
        ["latest_headless_tasks"] = new JsonArray(),
        ["latest_headless_task_run_seconds_before"] = null,
        ["gateway_connect_reasons"] = "AppSkeleton",
    };
}

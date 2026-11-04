namespace Slopsoil.Services.Jellyfin;

/// <summary>Builds the Jellyfin <c>Authorization: MediaBrowser …</c> header (port of <c>auth_header</c>).</summary>
public static class AuthHeader
{
    /// <summary>Device id used for the bot's own (non-SyncPlay) requests.</summary>
    public const string DefaultDeviceId = "slopsoil-discord-bot";

    /// <summary>Client name shown in the Jellyfin dashboard.</summary>
    public const string ClientName = "slopsoil";

    /// <summary>Client version sent with every request.</summary>
    public const string ClientVersion = "1.0";

    /// <summary>
    /// Header value for an API key or user access token (<paramref name="token"/> may be null for sign-in).
    /// Client/Device/DeviceId are required for user sign-in and make the bot show up in the dashboard.
    /// </summary>
    public static string Build(string? token, string deviceId = DefaultDeviceId)
    {
        var header = $"MediaBrowser Client=\"{ClientName}\", Device=\"slopsoil\", DeviceId=\"{deviceId}\", Version=\"{ClientVersion}\"";
        return string.IsNullOrEmpty(token) ? header : $"{header}, Token=\"{token}\"";
    }
}

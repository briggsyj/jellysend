using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Slopsoil.Common.Config;

/// <summary>One <c>STREAM_QUALITY</c> preset (<c>video_player._STREAM_PRESETS</c>).</summary>
public sealed record StreamPreset(string Resolution, double Fps, string Bitrate);

/// <summary>
/// Environment configuration with the Python bot's names, defaults and clamps
/// (contracts/configuration.md). Invalid values fall back to the default with a warning, never throw;
/// variables outside the subset are ignored.
/// </summary>
public sealed partial record SlopsoilOptions
{
    public const string DefaultQuality = "1080p";

    public static readonly IReadOnlyDictionary<string, StreamPreset> Presets = new Dictionary<string, StreamPreset>
    {
        ["720p"] = new("1280:720", 30.0, "10000k"),
        ["1080p"] = new("1920:1080", 60.0, "12000k"),
        ["4k"] = new("3840:2160", 60.0, "24000k"),
    };

    public string? DiscordToken { get; init; }

    public IReadOnlySet<ulong> AllowedUserIds { get; init; } = new HashSet<ulong>();

    /// <summary>Base URL with trailing slashes trimmed; null when unset.</summary>
    public string? JellyfinUrl { get; init; }

    public string? JellyfinApiKey { get; init; }

    public string? JellyfinUsername { get; init; }

    public string? JellyfinPassword { get; init; }

    public StreamPreset Quality { get; init; } = Presets[DefaultQuality];

    /// <summary>Scaler bounding box <c>W:H</c>.</summary>
    public string Resolution { get; init; } = Presets[DefaultQuality].Resolution;

    public double Fps { get; init; } = Presets[DefaultQuality].Fps;

    public string VideoBitrate { get; init; } = Presets[DefaultQuality].Bitrate;

    /// <summary>Fraction of the frame interval to spread a frame's packets over (0.0–0.95).</summary>
    public double PacketPace { get; init; }

    /// <summary>A/V offset in milliseconds (±5000).</summary>
    public int AvSyncMs { get; init; }

    /// <summary>Default of <see cref="StreamBufferSeconds"/> (spec 004).</summary>
    public const double DefaultStreamBufferSeconds = 5.0;

    /// <summary>
    /// Spec 006: play the original file from Jellyfin and transcode once in the bot (subtitles still use Jellyfin's
    /// transcode with burn-in). False = always let Jellyfin transcode to HLS first.
    /// </summary>
    public bool JellyfinDirectStream { get; init; } = true;

    /// <summary>Read-ahead of FFmpeg's output before and during playout, seconds (0–30; 0 = no read-ahead).</summary>
    public double StreamBufferSeconds { get; init; } = DefaultStreamBufferSeconds;

    /// <summary>Default of <see cref="EmptyChannelTimeout"/> (spec 003).</summary>
    public static readonly TimeSpan DefaultEmptyChannelTimeout = TimeSpan.FromMinutes(20);

    /// <summary>How long the bot stays (and streams) in a voice channel nobody else is in; zero = leave at once.</summary>
    public TimeSpan EmptyChannelTimeout { get; init; } = DefaultEmptyChannelTimeout;

    /// <summary>Jellyfin is usable when a URL and an API key or login are configured.</summary>
    public bool JellyfinConfigured =>
        JellyfinUrl is not null && (JellyfinApiKey is not null || (JellyfinUsername is not null && JellyfinPassword is not null));

    /// <summary>SyncPlay needs a user login (an API key has no user).</summary>
    public bool SyncPlayConfigured => JellyfinUrl is not null && JellyfinUsername is not null && JellyfinPassword is not null;

    public static SlopsoilOptions FromEnvironment(Action<string>? warn = null)
    {
        var env = new Dictionary<string, string?>();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            env[(string)e.Key] = e.Value as string;
        }

        return From(env, warn);
    }

    public static SlopsoilOptions From(IReadOnlyDictionary<string, string?> env, Action<string>? warn = null)
    {
        string? Get(string name) => env.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var quality = Presets[DefaultQuality];
        if (Get("STREAM_QUALITY")?.ToLowerInvariant() is { } q)
        {
            if (Presets.TryGetValue(q, out var preset))
            {
                quality = preset;
            }
            else
            {
                warn?.Invoke($"STREAM_QUALITY='{q}' unknown (want {string.Join('/', Presets.Keys)}); using {DefaultQuality}");
            }
        }

        var resolution = quality.Resolution;
        if (Get("STREAM_RESOLUTION")?.Replace('x', ':') is { } r)
        {
            if (ResolutionPattern().IsMatch(r))
            {
                resolution = r;
            }
            else
            {
                warn?.Invoke($"STREAM_RESOLUTION='{r}' invalid (want W:H); using preset");
            }
        }

        var fps = quality.Fps;
        if (Get("STREAM_FPS") is { } f)
        {
            if (!TryDouble(f, out var v))
            {
                warn?.Invoke($"STREAM_FPS='{f}' not a number; using preset");
            }
            else if (v is < 1.0 or > 60.0)
            {
                warn?.Invoke($"STREAM_FPS={f} out of range 1-60; using preset");
            }
            else
            {
                fps = v;
            }
        }

        var pace = 0.0;
        if (Get("STREAM_PACKET_PACE") is { } p)
        {
            if (TryDouble(p, out var v))
            {
                pace = Math.Clamp(v, 0.0, 0.95);
            }
            else
            {
                warn?.Invoke($"STREAM_PACKET_PACE='{p}' not a number; pacing disabled");
            }
        }

        var sync = 0;
        if (Get("STREAM_AV_SYNC_MS") is { } s)
        {
            if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
            {
                sync = (int)Math.Clamp(v, -5000, 5000);
            }
            else
            {
                warn?.Invoke($"STREAM_AV_SYNC_MS='{s}' not an integer; using 0");
            }
        }

        var buffer = DefaultStreamBufferSeconds;
        if (Get("STREAM_BUFFER_SECONDS") is { } b)
        {
            if (TryDouble(b, out var seconds) && seconds is >= 0 and <= 30)
            {
                buffer = seconds;
            }
            else
            {
                warn?.Invoke($"STREAM_BUFFER_SECONDS='{b}' invalid (want 0-30); using 5");
            }
        }

        var emptyTimeout = DefaultEmptyChannelTimeout;
        if (Get("EMPTY_CHANNEL_TIMEOUT_MINUTES") is { } e)
        {
            if (TryDouble(e, out var minutes) && minutes is >= 0 and <= 1440)
            {
                emptyTimeout = TimeSpan.FromMinutes(minutes);
            }
            else
            {
                warn?.Invoke($"EMPTY_CHANNEL_TIMEOUT_MINUTES='{e}' invalid (want 0-1440); using 20");
            }
        }

        var allowed = new HashSet<ulong>();
        foreach (var part in (Get("ALLOWED_USER_IDS") ?? "").Split(','))
        {
            var id = part.Trim();
            if (id.Length > 0 && id.All(char.IsAsciiDigit) && ulong.TryParse(id, CultureInfo.InvariantCulture, out var v))
            {
                allowed.Add(v);
            }
        }

        return new SlopsoilOptions
        {
            DiscordToken = Get("DISCORD_TOKEN"),
            AllowedUserIds = allowed,
            JellyfinUrl = Get("JELLYFIN_URL")?.TrimEnd('/'),
            JellyfinApiKey = Get("JELLYFIN_API_KEY"),
            JellyfinUsername = Get("JELLYFIN_USERNAME"),
            JellyfinPassword = Get("JELLYFIN_PASSWORD"),
            Quality = quality,
            Resolution = resolution,
            Fps = fps,
            VideoBitrate = Get("STREAM_VIDEO_BITRATE") ?? quality.Bitrate,
            PacketPace = pace,
            AvSyncMs = sync,
            StreamBufferSeconds = buffer,
            JellyfinDirectStream = ParseBool(Get("JELLYFIN_DIRECT_STREAM"), true, "JELLYFIN_DIRECT_STREAM", warn),
            EmptyChannelTimeout = emptyTimeout,
        };
    }

    /// <summary>Never print secrets: the token and password are masked.</summary>
    public override string ToString() =>
        $"SlopsoilOptions {{ Jellyfin = {JellyfinUrl ?? "(unset)"}, SyncPlay = {SyncPlayConfigured}, Admins = {AllowedUserIds.Count}, "
        + $"Resolution = {Resolution}, Fps = {Fps.ToString(CultureInfo.InvariantCulture)}, Bitrate = {VideoBitrate}, "
        + $"PacketPace = {PacketPace.ToString(CultureInfo.InvariantCulture)}, AvSyncMs = {AvSyncMs}, "
        + $"StreamBufferSeconds = {StreamBufferSeconds.ToString(CultureInfo.InvariantCulture)}, DirectStream = {JellyfinDirectStream}, "
        + $"EmptyChannelTimeout = {EmptyChannelTimeout.TotalMinutes.ToString(CultureInfo.InvariantCulture)} min }}";

    private static bool ParseBool(string? raw, bool fallback, string name, Action<string>? warn)
    {
        switch (raw?.ToLowerInvariant())
        {
            case null:
                return fallback;
            case "1" or "true" or "yes" or "on":
                return true;
            case "0" or "false" or "no" or "off":
                return false;
            default:
                warn?.Invoke($"{name}='{raw}' invalid (want true/false); using {(fallback ? "true" : "false")}");
                return fallback;
        }
    }

    // Python float(): decimal or exponent, no thousands separators; also rejects NaN/inf like the range checks would.
    private static bool TryDouble(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    [GeneratedRegex(@"^\d{2,5}:\d{2,5}$")]
    private static partial Regex ResolutionPattern();
}

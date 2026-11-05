using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Slopsoil.Common.Config;
using Slopsoil.Common.Logging;

namespace Slopsoil.Services.Jellyfin;

/// <summary>
/// Jellyfin REST client (port of <c>services/jellyfin.py</c>): item search, season/episode lookup, PlaybackInfo →
/// HLS stream URL, and transcode teardown. Authenticates with an API key, or signs in lazily with a
/// username/password on first use (under a lock, once).
/// </summary>
public sealed class JellyfinClient : IDisposable
{
    /// <summary>Default per-request timeout.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>PlaybackInfo timeout (the server may start a transcode).</summary>
    public static readonly TimeSpan PlaybackInfoTimeout = TimeSpan.FromSeconds(15);

    private const string SearchFields = "ProductionYear,SeriesName,SeasonName,IndexNumber,ParentIndexNumber";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly string _password;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private volatile string _token;
    private volatile string? _userId;

    /// <summary>Create a client.</summary>
    /// <param name="url">Server base URL; a trailing slash is trimmed.</param>
    /// <param name="apiKey">API key; when empty and a login is given, the client signs in on first use.</param>
    /// <param name="username">Jellyfin user (required for SyncPlay).</param>
    /// <param name="password">Jellyfin password.</param>
    /// <param name="http">HTTP client (tests inject a fake handler); a private one is created when null.</param>
    /// <param name="logger">Logger; <see cref="NullLogger"/> when null.</param>
    /// <param name="time">Clock used for request timeouts.</param>
    public JellyfinClient(
        string url,
        string? apiKey = null,
        string? username = null,
        string? password = null,
        HttpClient? http = null,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        BaseUrl = url.TrimEnd('/');
        Username = username ?? "";
        _password = password ?? "";
        _token = apiKey ?? "";
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _log = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Server base URL without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>Configured Jellyfin username ("" when none).</summary>
    public string Username { get; }

    /// <summary>True when a username and password are configured (required for SyncPlay).</summary>
    public bool HasUserCredentials => Username.Length > 0 && _password.Length > 0;

    /// <summary>The API key or signed-in access token in use ("" before lazy sign-in).</summary>
    internal string Token => _token;

    /// <summary>The cached user id, if resolved.</summary>
    internal string? CachedUserId
    {
        get => _userId;
        set => _userId = value;
    }

    /// <summary>The HTTP client, shared with <see cref="SyncPlay.SyncPlayGroup"/>.</summary>
    internal HttpClient Http => _http;

    /// <summary>Clock used for timeouts, shared with <see cref="SyncPlay.SyncPlayGroup"/>.</summary>
    internal TimeProvider Time => _time;

    /// <summary>Build a client from configuration, or null when Jellyfin is not configured.</summary>
    public static JellyfinClient? FromOptions(SlopsoilOptions options, HttpClient? http = null, ILogger? logger = null, TimeProvider? time = null) =>
        options.JellyfinConfigured
            ? new JellyfinClient(options.JellyfinUrl!, options.JellyfinApiKey, options.JellyfinUsername, options.JellyfinPassword, http, logger, time)
            {
                PreferDirectStream = options.JellyfinDirectStream,
            }
            : null;

    /// <summary>
    /// Spec 006: when no subtitles are selected and the source is a plain file, return Jellyfin's static stream of the
    /// original (the bot then transcodes once) instead of Jellyfin's HLS transcode.
    /// </summary>
    public bool PreferDirectStream { get; init; }

    /// <summary>
    /// Spec 011: whether the bot's FFmpeg can decode a source's formats. A direct stream it cannot decode (e.g. HEVC
    /// on Fedora's ffmpeg-free) falls back to Jellyfin's transcode. Null = every format is assumed decodable.
    /// </summary>
    public Func<DirectStreamFormat, bool>? CanDecode { get; set; }

    /// <summary>
    /// Sign in with the configured username/password for <paramref name="deviceId"/>. Each device id gets its own
    /// Jellyfin session, which matters for SyncPlay (a session can only be in one group; use <c>slopsoil-&lt;guildId&gt;</c>).
    /// </summary>
    /// <exception cref="JellyfinException">The request failed or the response was malformed.</exception>
    public async Task<JellyfinSession> AuthenticateAsync(string deviceId = AuthHeader.DefaultDeviceId, CancellationToken ct = default)
    {
        var body = new JsonObject { ["Username"] = Username, ["Pw"] = _password };
        var data = await SendAsync(HttpMethod.Post, "/Users/AuthenticateByName", body, AuthHeader.Build(null, deviceId), RequestTimeout, ct)
            .ConfigureAwait(false);
        try
        {
            return new JellyfinSession(
                data.GetProperty("AccessToken").GetString()!,
                data.GetProperty("User").GetProperty("Id").GetString()!);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new JellyfinException("Jellyfin sign-in returned an unexpected response", ex);
        }
    }

    /// <summary>Search movies, series and episodes matching <paramref name="query"/>.</summary>
    /// <exception cref="JellyfinException">The request failed.</exception>
    public async Task<IReadOnlyList<MediaItem>> SearchAsync(string query, int limit = 25, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        var q = Query(
            ("searchTerm", query),
            ("Recursive", "true"),
            ("Limit", limit.ToString(CultureInfo.InvariantCulture)),
            ("IncludeItemTypes", "Movie,Series,Episode"),
            ("Fields", SearchFields));
        return await GetItemsAsync($"/Items?{q}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Find an episode by series name and season/episode numbers: search the series by name, then walk
    /// seasons → episodes (Jellyfin's searchTerm matches episode titles, not series names). One hit per matching series.
    /// </summary>
    /// <exception cref="JellyfinException">A request failed.</exception>
    public async Task<IReadOnlyList<MediaItem>> FindEpisodeAsync(string show, int season, int episode, CancellationToken ct = default)
    {
        var matches = new List<MediaItem>();
        foreach (var series in (await SearchAsync(show, ct: ct).ConfigureAwait(false)).Where(r => r.IsSeries))
        {
            var seasons = await GetSeasonsAsync(series.Id, ct).ConfigureAwait(false);
            var target = seasons.FirstOrDefault(s => s.IndexNumber == season);
            if (target is null)
            {
                continue;
            }

            var episodes = await GetEpisodesAsync(series.Id, target.Id, ct).ConfigureAwait(false);
            matches.AddRange(episodes.Where(e => e.IndexNumber == episode));
        }

        return matches;
    }

    /// <summary>All seasons of a series, ordered by IndexNumber (missing = 0, stable).</summary>
    /// <exception cref="JellyfinException">The request failed.</exception>
    public async Task<IReadOnlyList<MediaItem>> GetSeasonsAsync(string seriesId, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        var items = await GetItemsAsync($"/Shows/{Uri.EscapeDataString(seriesId)}/Seasons?{Query(("Fields", "Id,Name,IndexNumber"))}", ct)
            .ConfigureAwait(false);
        return [.. items.OrderBy(s => s.IndexNumber ?? 0)];
    }

    /// <summary>All episodes of a season, ordered by IndexNumber (missing = 0, stable).</summary>
    /// <exception cref="JellyfinException">The request failed.</exception>
    public async Task<IReadOnlyList<MediaItem>> GetEpisodesAsync(string seriesId, string seasonId, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        var q = Query(("SeasonId", seasonId), ("Fields", "Id,Name,IndexNumber,ParentIndexNumber,SeriesName"));
        var items = await GetItemsAsync($"/Shows/{Uri.EscapeDataString(seriesId)}/Episodes?{q}", ct).ConfigureAwait(false);
        return [.. items.OrderBy(e => e.IndexNumber ?? 0)];
    }

    /// <summary>Fetch one item by id, or null when it does not exist or the request fails (logged).</summary>
    public async Task<MediaItem?> GetItemAsync(string itemId, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        try
        {
            var q = Query(("Ids", itemId), ("Fields", "ProductionYear,SeriesName,IndexNumber,ParentIndexNumber"));
            var items = await GetItemsAsync($"/Items?{q}", ct).ConfigureAwait(false);
            return items.Count > 0 ? items[0] : null;
        }
        catch (JellyfinException ex)
        {
            _log.LogWarning("Jellyfin: could not fetch item {ItemId}: {Error}", itemId, Describe(ex));
            return null;
        }
    }

    /// <summary>Start a playback session and return just the stream URL (null on failure).</summary>
    public async Task<string?> GetStreamUrlAsync(string itemId, CancellationToken ct = default) =>
        (await StartPlaybackAsync(itemId, null, ct).ConfigureAwait(false))?.Url;

    /// <summary>Subtitle formats Jellyfin may burn in (method Encode) when a subtitle track is selected.</summary>
    internal static readonly IReadOnlyList<string> BurnInFormats =
        ["srt", "subrip", "ass", "ssa", "vtt", "webvtt", "mov_text", "pgssub", "pgs", "dvdsub", "vobsub", "dvbsub", "sub", "smi", "ttml"];

    /// <summary>
    /// Start a playback session via <c>POST /Items/{id}/PlaybackInfo</c> with an H.264/AAC HLS DeviceProfile and no
    /// subtitles. The returned URL is absolute, has <c>SubtitleStreamIndex=-1</c> forced (the user's server profile
    /// can override the body's -1 and burn subtitles in) and <c>api_key</c> appended so FFmpeg can fetch segments.
    /// Returns null when the request fails or yields no usable source (logged).
    /// </summary>
    public async Task<PlaybackStream?> StartPlaybackAsync(string itemId, TrackSelection? tracks = null, CancellationToken ct = default, bool allowDirect = true)
    {
        tracks ??= TrackSelection.Default;
        var subtitle = tracks.SubtitleIndex ?? -1;
        var userId = await GetUserIdAsync(ct).ConfigureAwait(false);
        var payload = new JsonObject
        {
            // -1 + an empty SubtitleProfiles list: no soft subtitle track and no burned-in subtitles. A selected
            // subtitle track is burned in by Jellyfin's transcoder (every format with method Encode, spec 005).
            ["SubtitleStreamIndex"] = subtitle,
            ["DeviceProfile"] = new JsonObject
            {
                ["MaxStreamingBitrate"] = 8_000_000,
                ["TranscodingProfiles"] = new JsonArray(new JsonObject
                {
                    ["Container"] = "ts",
                    ["Type"] = "Video",
                    ["Protocol"] = "hls",
                    ["AudioCodec"] = "aac",
                    ["VideoCodec"] = "h264",
                    ["MaxAudioChannels"] = 2,
                    ["Context"] = "Streaming",
                }),
                ["DirectPlayProfiles"] = new JsonArray(),
                ["CodecProfiles"] = new JsonArray(),
                ["SubtitleProfiles"] = subtitle < 0
                    ? new JsonArray()
                    : new JsonArray([.. BurnInFormats.Select(f => (JsonNode)new JsonObject { ["Format"] = f, ["Method"] = "Encode" })]),
            },
        };
        if (tracks.AudioIndex is { } audio)
        {
            payload["AudioStreamIndex"] = audio;
        }
        var query = new List<(string, string)> { ("DeviceId", AuthHeader.DefaultDeviceId) };
        if (!string.IsNullOrEmpty(userId))
        {
            payload["UserId"] = userId;
            query.Add(("userId", userId));
        }

        JsonElement result;
        try
        {
            result = await SendAsync(
                HttpMethod.Post,
                $"/Items/{Uri.EscapeDataString(itemId)}/PlaybackInfo?{Query([.. query])}",
                payload,
                AuthHeader.Build(_token),
                PlaybackInfoTimeout,
                ct).ConfigureAwait(false);
        }
        catch (JellyfinException ex)
        {
            _log.LogWarning("PlaybackInfo failed for item {ItemId}: {Error}", itemId, Describe(ex));
            return null;
        }

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("MediaSources", out var sources)
            || sources.ValueKind != JsonValueKind.Array
            || sources.GetArrayLength() == 0)
        {
            _log.LogWarning("PlaybackInfo returned no media sources for item {ItemId}", itemId);
            return null;
        }

        var source = sources[0];
        var whyTranscode = WhyNotDirect(itemId, source, tracks, allowDirect);
        if (whyTranscode is null && DirectSourceId(source) is { } sourceId)
        {
            var (directAudio, directCodec) = StreamMetadata(source, transcoding: false);
            whyTranscode = directCodec is null ? "video codec not reported" : null;
            if (directCodec is not null)
            {
                var direct = $"{BaseUrl}/Videos/{Uri.EscapeDataString(itemId)}/stream?{Query(("static", "true"), ("MediaSourceId", sourceId))}";
                return new PlaybackStream(
                    $"{direct}&api_key={_token}",
                    NonEmptyString(result, "PlaySessionId"),
                    directAudio,
                    directCodec,
                    RuntimeOf(source))
                {
                    Tracks = ParseTracks(source),
                    Selection = tracks,
                    IsDirect = true,
                    FrameRate = FrameRateOf(source),
                };
            }
        }

        // Jellyfin's HLS transcoding URL (H.264/AAC); fall back to its direct stream URL.
        var transcodingUrl = NonEmptyString(source, "TranscodingUrl");
        var raw = transcodingUrl ?? NonEmptyString(source, "DirectStreamUrl");
        if (raw is null)
        {
            _log.LogWarning("No stream URL in PlaybackInfo for item {ItemId}", itemId);
            return null;
        }

        var full = raw.StartsWith("http://", StringComparison.Ordinal) || raw.StartsWith("https://", StringComparison.Ordinal)
            ? raw
            : BaseUrl + raw;
        full = ForceTracks(full, subtitle, tracks.AudioIndex, out var queryPlaySession);
        var (hasAudio, codec) = StreamMetadata(source, transcodingUrl is not null);
        long? runtimeTicks = source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty("RunTimeTicks", out var rt)
            && rt.ValueKind == JsonValueKind.Number
            && rt.TryGetInt64(out var t)
            && t > 0 ? t : null;
        return new PlaybackStream(
            $"{full}&api_key={_token}",
            NonEmptyString(result, "PlaySessionId") ?? queryPlaySession,
            hasAudio,
            codec,
            runtimeTicks is { } ticks ? JellyfinTicks.ToSeconds(ticks) : null)
        {
            Tracks = ParseTracks(source),
            Selection = tracks,
            FrameRate = FrameRateOf(source),
            TranscodeReason = whyTranscode ?? "Jellyfin chose it",
        };
    }

    /// <summary>
    /// Why the original file cannot be streamed directly (spec 017: shown in the stats), or null when it can.
    /// </summary>
    private string? WhyNotDirect(string itemId, JsonElement source, TrackSelection tracks, bool allowDirect)
    {
        string? why = null;
        if (!PreferDirectStream)
        {
            why = "direct stream turned off";
        }
        else if (!allowDirect)
        {
            why = "the original file failed to start";
        }
        else if (tracks.SubtitleIndex is not null)
        {
            why = "burning in subtitles";
        }
        else if (DirectSourceId(source) is null)
        {
            why = "not a plain file (disc image or remote source)";
        }
        else if (PlayingAudio(ParseTracks(source), tracks.AudioIndex) is { IsExternal: true })
        {
            // The original file only holds its embedded tracks: an external audio file needs Jellyfin's transcode.
            why = "external audio track";
        }
        else if (CanDecode is { } canDecode && FormatOf(source, tracks.AudioIndex) is var format && !canDecode(format))
        {
            why = $"bot can't decode {format}";
        }

        if (why is not null && PreferDirectStream)
        {
            _log.LogInformation("Jellyfin: using Jellyfin's transcode for item {ItemId} instead of the original file: {Reason}", itemId, why);
        }

        return why;
    }

    /// <summary>
    /// The audio track that plays: <paramref name="audioIndex"/> (Jellyfin index) when given, else Jellyfin's default
    /// audio track, else the first one. Null without audio tracks.
    /// </summary>
    public static MediaTrack? PlayingAudio(IReadOnlyList<MediaTrack> tracks, int? audioIndex)
    {
        var audio = tracks.Where(t => t.IsAudio).ToList();
        return audio.Find(t => t.Index == audioIndex) ?? audio.Find(t => t.IsDefault) ?? audio.FirstOrDefault();
    }

    /// <summary>
    /// The formats a direct stream of <paramref name="source"/> would hand FFmpeg: the first video stream and the
    /// audio track that would play (<paramref name="audioIndex"/>, else Jellyfin's default, else the first).
    /// </summary>
    internal static DirectStreamFormat FormatOf(JsonElement source, int? audioIndex)
    {
        if (source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("MediaStreams", out var streams)
            || streams.ValueKind != JsonValueKind.Array)
        {
            return new DirectStreamFormat(null, null, false, null);
        }

        var all = streams.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object).ToList();
        var video = all.Find(s => NonEmptyString(s, "Type") == "Video");
        var audios = all.Where(s => NonEmptyString(s, "Type") == "Audio").ToList();
        var audio = audios.Find(s => audioIndex is { } i && s.TryGetProperty("Index", out var x) && x.TryGetInt32(out var n) && n == i);
        if (audio.ValueKind != JsonValueKind.Object)
        {
            audio = audios.Find(s => s.TryGetProperty("IsDefault", out var d) && d.ValueKind == JsonValueKind.True);
        }

        if (audio.ValueKind != JsonValueKind.Object && audios.Count > 0)
        {
            audio = audios[0];
        }

        int? bitDepth = video.ValueKind == JsonValueKind.Object && video.TryGetProperty("BitDepth", out var b) && b.TryGetInt32(out var depth) ? depth : null;
        var interlaced = video.ValueKind == JsonValueKind.Object && video.TryGetProperty("IsInterlaced", out var il) && il.ValueKind == JsonValueKind.True;
        return new DirectStreamFormat(
            NonEmptyString(video, "Codec")?.ToLowerInvariant(), bitDepth, interlaced, NonEmptyString(audio, "Codec")?.ToLowerInvariant());
    }

    /// <summary>
    /// The first video stream's frame rate: AverageFrameRate (right for variable-rate files), else RealFrameRate.
    /// Null when absent or not positive.
    /// </summary>
    internal static double? FrameRateOf(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("MediaStreams", out var streams)
            || streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var s in streams.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object || NonEmptyString(s, "Type") != "Video")
            {
                continue;
            }

            foreach (var name in (string[])["AverageFrameRate", "RealFrameRate"])
            {
                if (s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var fps) && fps > 0)
                {
                    return fps;
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// The media source id when the source can be streamed as the original file: a local file (not a Blu-ray/DVD
    /// folder or ISO, not a remote/strm source). Null otherwise (spec 006 falls back to Jellyfin's transcode).
    /// </summary>
    internal static string? DirectSourceId(JsonElement source)
    {
        if (NonEmptyString(source, "Id") is not { } id || NonEmptyString(source, "Protocol") is not "File")
        {
            return null;
        }

        return NonEmptyString(source, "VideoType") is null or "VideoFile" && NonEmptyString(source, "IsoType") is null ? id : null;
    }

    private static double? RuntimeOf(JsonElement source) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty("RunTimeTicks", out var rt) && rt.ValueKind == JsonValueKind.Number
        && rt.TryGetInt64(out var t) && t > 0 ? JellyfinTicks.ToSeconds(t) : null;

    /// <summary>Audio and subtitle tracks of a media source, in index order (spec 005).</summary>
    internal static IReadOnlyList<MediaTrack> ParseTracks(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("MediaStreams", out var streams) || streams.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var tracks = new List<MediaTrack>();
        var embeddedAudio = 0;
        var embeddedSubtitles = 0;
        foreach (var s in streams.EnumerateArray())
        {
            var type = NonEmptyString(s, "Type");
            if (type is not ("Audio" or "Subtitle"))
            {
                continue;
            }

            // Count every embedded stream of the kind (even one without a usable Index): the ordinal is FFmpeg's view.
            var external = Flag(s, "IsExternal");
            int? ordinal = external ? null : type == "Audio" ? embeddedAudio++ : embeddedSubtitles++;
            if (!s.TryGetProperty("Index", out var i) || !i.TryGetInt32(out var index))
            {
                continue;
            }

            var language = NonEmptyString(s, "Language");
            var codec = NonEmptyString(s, "Codec")?.ToUpperInvariant();
            var label = NonEmptyString(s, "DisplayTitle")
                ?? string.Join(" — ", new[] { language ?? "Unknown", codec }.Where(x => x is not null));
            tracks.Add(new MediaTrack(index, type == "Audio", language, label, Flag(s, "IsDefault"), Flag(s, "IsForced"), external, ordinal));
        }

        return tracks;

        static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Tell Jellyfin to kill the transcode of a finished session (<c>DELETE /Videos/ActiveEncodings</c>); otherwise
    /// the server keeps transcoding until its inactivity timeout. No-op for a null/empty id; failures are logged and swallowed.
    /// </summary>
    public async Task StopEncodingAsync(string? playSessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playSessionId))
        {
            return;
        }

        try
        {
            var q = Query(("deviceId", AuthHeader.DefaultDeviceId), ("playSessionId", playSessionId));
            await SendAsync(HttpMethod.Delete, $"/Videos/ActiveEncodings?{q}", null, AuthHeader.Build(_token), RequestTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (JellyfinException ex)
        {
            _log.LogDebug("Jellyfin: stop_encoding({PlaySessionId}) failed: {Error}", playSessionId, Describe(ex));
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _authLock.Dispose();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>Sign in on first use when no API key is configured (once, under a lock).</summary>
    internal async Task EnsureAuthAsync(CancellationToken ct)
    {
        if (_token.Length > 0 || !HasUserCredentials)
        {
            return;
        }

        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token.Length > 0)
            {
                return;
            }

            var session = await AuthenticateAsync(ct: ct).ConfigureAwait(false);
            _userId = session.UserId;
            _token = session.AccessToken;
            _log.LogInformation("Jellyfin: signed in as {Username}", Username);
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>
    /// The signed-in user, or the first user of <c>/Users</c> (cached). PlaybackInfo needs a UserId even with an
    /// API key (400 otherwise). Null when the list cannot be fetched (logged).
    /// </summary>
    internal async Task<string?> GetUserIdAsync(CancellationToken ct)
    {
        await EnsureAuthAsync(ct).ConfigureAwait(false);
        if (_userId is { } cached)
        {
            return cached;
        }

        try
        {
            var users = await SendAsync(HttpMethod.Get, "/Users", null, AuthHeader.Build(_token), RequestTimeout, ct).ConfigureAwait(false);
            if (users.ValueKind == JsonValueKind.Array && users.GetArrayLength() > 0)
            {
                _userId = users[0].GetProperty("Id").GetString();
                _log.LogInformation("Jellyfin: resolved user ID {UserId}", _userId);
                return _userId;
            }
        }
        catch (Exception ex) when (ex is JellyfinException or KeyNotFoundException or InvalidOperationException)
        {
            _log.LogWarning("Jellyfin: could not fetch user list: {Error}", Describe(ex));
        }

        return null;
    }

    /// <summary>
    /// Rewrite the query so <c>SubtitleStreamIndex=-1</c> (first value per key kept, like <c>parse_qs</c> →
    /// <c>urlencode</c>), returning the URL's <c>PlaySessionId</c> query value if any.
    /// </summary>
    internal static string ForceNoSubtitles(string url, out string? playSessionId) => ForceTracks(url, -1, null, out playSessionId);

    /// <summary>
    /// Rewrite the query so <c>SubtitleStreamIndex</c> (and <c>AudioStreamIndex</c> when given) say what we asked
    /// for — the user's server profile could otherwise override them — returning the URL's <c>PlaySessionId</c>.
    /// </summary>
    internal static string ForceTracks(string url, int subtitleIndex, int? audioIndex, out string? playSessionId)
    {
        var fragment = "";
        var hash = url.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            fragment = url[hash..];
            url = url[..hash];
        }

        var path = url;
        var query = "";
        var mark = url.IndexOf('?', StringComparison.Ordinal);
        if (mark >= 0)
        {
            path = url[..mark];
            query = url[(mark + 1)..];
        }

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var key = FormDecode(eq >= 0 ? part[..eq] : part);
            var value = eq >= 0 ? FormDecode(part[(eq + 1)..]) : "";
            if (!pairs.Exists(p => p.Key == key))
            {
                pairs.Add(new(key, value));
            }
        }

        void Set(string key, string value)
        {
            var index = pairs.FindIndex(p => p.Key == key);
            if (index >= 0)
            {
                pairs[index] = new(key, value);
            }
            else
            {
                pairs.Add(new(key, value));
            }
        }

        Set("SubtitleStreamIndex", subtitleIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (audioIndex is { } audio)
        {
            Set("AudioStreamIndex", audio.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        playSessionId = pairs.Find(p => p.Key == "PlaySessionId").Value;
        return $"{path}?{Query([.. pairs.Select(p => (p.Key, p.Value))])}{fragment}";
    }

    /// <summary><c>_stream_metadata</c>: (HasAudio, VideoCodec) from a media source's MediaStreams.</summary>
    internal static (bool? HasAudio, string? VideoCodec) StreamMetadata(JsonElement source, bool transcoding)
    {
        if (source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("MediaStreams", out var streams)
            || streams.ValueKind != JsonValueKind.Array
            || streams.GetArrayLength() == 0)
        {
            return (null, null);
        }

        var all = streams.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object).ToList();
        var hasAudio = all.Exists(s => NonEmptyString(s, "Type") == "Audio");
        var video = all.FindIndex(s => NonEmptyString(s, "Type") == "Video");
        if (video < 0)
        {
            return (hasAudio, null); // no video track: let ffprobe decide/reject
        }

        if (transcoding)
        {
            return (hasAudio, "h264"); // we asked Jellyfin to transcode to H.264
        }

        return all[video].TryGetProperty("Codec", out var codec) && codec.ValueKind == JsonValueKind.String
            ? (hasAudio, codec.GetString()!.ToLowerInvariant())
            : (hasAudio, null);
    }

    /// <summary>Form-encode pairs like Python's <c>urlencode</c> (space → <c>+</c>).</summary>
    internal static string Query(params (string Key, string Value)[] pairs) =>
        string.Join('&', pairs.Select(p => $"{FormEncode(p.Key)}={FormEncode(p.Value)}"));

    private static string FormEncode(string s) => Uri.EscapeDataString(s).Replace("%20", "+", StringComparison.Ordinal);

    private static string FormDecode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    private static string? NonEmptyString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
        && v.GetString() is { Length: > 0 } s ? s : null;

    private static string Describe(Exception ex) =>
        Redactor.RedactText(ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message);

    private async Task<IReadOnlyList<MediaItem>> GetItemsAsync(string pathAndQuery, CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Get, pathAndQuery, null, AuthHeader.Build(_token), RequestTimeout, ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        try
        {
            return items.Deserialize<List<MediaItem>>(JsonOptions)!;
        }
        catch (JsonException ex)
        {
            throw new JellyfinException("Jellyfin returned malformed items", ex);
        }
    }

    /// <summary>
    /// Send one request with the given Authorization header and timeout; returns the parsed JSON body
    /// (<c>default</c> when empty). Failures other than caller cancellation become <see cref="JellyfinException"/>.
    /// </summary>
    private async Task<JsonElement> SendAsync(HttpMethod method, string pathAndQuery, JsonNode? body, string authorization, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var request = new HttpRequestMessage(method, BaseUrl + pathAndQuery);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        var what = $"{method} {pathAndQuery.Split('?')[0]}";
        try
        {
            using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new JellyfinException($"Jellyfin {what} → HTTP {(int)response.StatusCode}");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return default;
            }

            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new JellyfinException($"Jellyfin {what} timed out", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            throw new JellyfinException($"Jellyfin {what} failed", ex);
        }
    }
}

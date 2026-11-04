using System.Text.Json.Serialization;

namespace Slopsoil.Services.Jellyfin;

/// <summary>Jellyfin ticks: 100 ns units (10,000,000 per second).</summary>
public static class JellyfinTicks
{
    /// <summary>Ticks per second.</summary>
    public const long PerSecond = 10_000_000;

    /// <summary>Seconds → ticks, rounded half-to-even like Python's <c>round</c> (<c>seconds_to_ticks</c>).</summary>
    public static long FromSeconds(double seconds) => (long)Math.Round(seconds * PerSecond);

    /// <summary>Ticks → seconds.</summary>
    public static double ToSeconds(long ticks) => (double)ticks / PerSecond;
}

/// <summary>
/// A Jellyfin <c>BaseItemDto</c> subset: what search, season/episode listing and <c>get_item</c> return and what
/// the <c>!media</c> pickers and labels use. Unknown fields are ignored.
/// </summary>
public sealed record MediaItem
{
    /// <summary>Jellyfin item id.</summary>
    public string Id { get; init; } = "";

    /// <summary>Display name (title, season name, or episode title).</summary>
    public string? Name { get; init; }

    /// <summary><c>Movie</c>, <c>Series</c>, <c>Season</c> or <c>Episode</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Release year, when known.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Series name (episodes only).</summary>
    public string? SeriesName { get; init; }

    /// <summary>Series id (episodes and seasons).</summary>
    public string? SeriesId { get; init; }

    /// <summary>Season name (episodes only).</summary>
    public string? SeasonName { get; init; }

    /// <summary>Season id (episodes only).</summary>
    public string? SeasonId { get; init; }

    /// <summary>Season number for an episode.</summary>
    public int? ParentIndexNumber { get; init; }

    /// <summary>Episode number for an episode; season number for a season.</summary>
    public int? IndexNumber { get; init; }

    /// <summary>Runtime in ticks, when known.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>True for <c>Type == "Series"</c> (never played directly; the picker resolves an episode).</summary>
    [JsonIgnore]
    public bool IsSeries => Type == "Series";

    /// <summary>Runtime in seconds, or null when unknown or not positive (<c>_runtime_seconds</c>).</summary>
    [JsonIgnore]
    public double? RuntimeSeconds => RunTimeTicks is > 0 and { } t ? JellyfinTicks.ToSeconds(t) : null;
}

/// <summary>
/// A started Jellyfin playback session. <see cref="HasAudio"/> / <see cref="VideoCodec"/> come from PlaybackInfo's
/// MediaStreams so the caller can skip ffprobe; they are null when Jellyfin did not report them (VideoCodec is
/// also null when there is no video stream — let ffprobe decide/reject).
/// </summary>
/// <param name="Url">Absolute stream URL including <c>api_key</c>; never log it unredacted.</param>
/// <param name="PlaySessionId">Session id for <see cref="JellyfinClient.StopEncodingAsync"/>.</param>
/// <param name="HasAudio">Whether the source has an audio stream, if reported.</param>
/// <param name="VideoCodec">Lower-case codec FFmpeg will receive (<c>h264</c> when transcoding), if reported.</param>
/// <param name="RuntimeSeconds">Media source runtime in seconds, if reported and positive.</param>
public sealed record PlaybackStream(string Url, string? PlaySessionId, bool? HasAudio, string? VideoCodec, double? RuntimeSeconds = null)
{
    /// <summary>The source's audio and subtitle tracks (spec 005), in Jellyfin's index order.</summary>
    public IReadOnlyList<MediaTrack> Tracks { get; init; } = [];

    /// <summary>The tracks this session was started with.</summary>
    public TrackSelection Selection { get; init; } = TrackSelection.Default;
}

/// <summary>An audio or subtitle track of a media source (from PlaybackInfo's MediaStreams).</summary>
/// <param name="Index">Jellyfin stream index (what <c>AudioStreamIndex</c> / <c>SubtitleStreamIndex</c> take).</param>
/// <param name="IsAudio">Audio track; otherwise a subtitle track.</param>
/// <param name="Language">ISO language code if tagged (e.g. <c>eng</c>).</param>
/// <param name="Label">Human-readable name (Jellyfin's DisplayTitle, else language + codec).</param>
public sealed record MediaTrack(int Index, bool IsAudio, string? Language, string Label, bool IsDefault, bool IsForced);

/// <summary>Which tracks to play: <see cref="AudioIndex"/> null = the source's default audio; <see cref="SubtitleIndex"/> null = no subtitles.</summary>
public sealed record TrackSelection(int? AudioIndex, int? SubtitleIndex)
{
    public static TrackSelection Default { get; } = new(null, null);
}

/// <summary>Result of <c>/Users/AuthenticateByName</c>.</summary>
/// <param name="AccessToken">User access token (use as <c>Token</c> / <c>api_key</c>).</param>
/// <param name="UserId">Signed-in user's id.</param>
public sealed record JellyfinSession(string AccessToken, string UserId);

/// <summary>A Jellyfin HTTP request failed (network error, timeout, HTTP error status or malformed JSON).</summary>
public sealed class JellyfinException : Exception
{
    /// <summary>Create with a message.</summary>
    public JellyfinException(string message)
        : base(message)
    {
    }

    /// <summary>Create with a message and the underlying failure.</summary>
    public JellyfinException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

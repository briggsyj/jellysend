using Slopsoil.Services.Jellyfin;
using Slopsoil.Services.Media;
using Slopsoil.Services.SyncPlay;

namespace Slopsoil.Commands.Media;

/// <summary>The Jellyfin operations the media commands use (Host adapts JellyfinClient + HLS + ffprobe + SyncPlay).</summary>
public interface IMediaLibrary
{
    /// <summary>A username/password is configured, so SyncPlay is available.</summary>
    bool HasUserCredentials { get; }

    Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken ct);

    Task<IReadOnlyList<MediaItem>> FindEpisodeAsync(string show, int season, int episode, CancellationToken ct);

    Task<IReadOnlyList<MediaItem>> GetSeasonsAsync(string seriesId, CancellationToken ct);

    Task<IReadOnlyList<MediaItem>> GetEpisodesAsync(string seriesId, string seasonId, CancellationToken ct);

    Task<MediaItem?> GetItemAsync(string itemId, CancellationToken ct);

    Task<PlaybackStream?> StartPlaybackAsync(string itemId, CancellationToken ct);

    Task StopEncodingAsync(string? playSessionId, CancellationToken ct);

    /// <summary>HLS master playlist → highest-bandwidth variant URL (or the URL unchanged).</summary>
    Task<string> ResolveVariantAsync(string url, CancellationToken ct);

    Task<ProbeResult?> ProbeAsync(string url, CancellationToken ct);

    ISyncPlay CreateSyncPlay(
        ulong guildId, Func<SyncPlayCommand, CancellationToken, Task> onCommand, Func<SyncPlayQueue, CancellationToken, Task> onPlayQueue);
}

/// <summary>A per-guild SyncPlay group (implemented by <see cref="SyncPlayGroup"/>).</summary>
public interface ISyncPlay
{
    string? GroupName { get; }

    bool Closed { get; }

    Task OpenAsync(string groupName, CancellationToken ct);

    Task CloseAsync(CancellationToken ct);

    Task SetQueueAsync(string itemId, double startSeconds, CancellationToken ct);

    Task PauseAsync(CancellationToken ct);

    Task UnpauseAsync(CancellationToken ct);

    Task SeekAsync(double seconds, CancellationToken ct);
}

/// <summary>Formatting of library items (<c>_fmt_item</c>).</summary>
public static class MediaLabels
{
    public static string Format(MediaItem item)
    {
        var name = item.Name ?? "Unknown";
        if (item.Type == "Episode")
        {
            var series = item.SeriesName ?? "";
            var code = item.ParentIndexNumber is { } s && item.IndexNumber is { } e ? $"S{s:00}E{e:00}" : "";
            return code.Length > 0 ? $"{series} — {code} — {name}" : $"{series} — {name}";
        }

        return item.ProductionYear is { } year and not 0 ? $"{name} ({year})" : name;
    }
}

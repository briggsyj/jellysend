using JellySend.Commands.Media;
using JellySend.Commands.Routing;
using JellySend.Commands.Voice;
using JellySend.Services.Jellyfin;
using JellySend.Services.Media;
using JellySend.Services.SyncPlay;
using JellySend.Streaming.Engine;
using JellySend.Tests.Discord;
using JellySend.Tests.Streaming;

namespace JellySend.Tests.Commands;

internal sealed class FakeSyncPlay(Func<SyncPlayCommand, CancellationToken, Task> onCommand, Func<SyncPlayQueue, CancellationToken, Task> onQueue) : ISyncPlay
{
    public List<string> Calls { get; } = [];

    public bool FailOpen { get; set; }

    public string? GroupName { get; private set; }

    public bool Closed { get; set; }

    public Task Command(string command, double? position) => onCommand(new SyncPlayCommand(command, position), default);

    public Task Queue(string? item, double start) => onQueue(new SyncPlayQueue(item, start, true), default);

    public Task OpenAsync(string groupName, CancellationToken ct)
    {
        Calls.Add($"open {groupName}");
        if (FailOpen)
        {
            throw new InvalidOperationException("denied");
        }

        GroupName = groupName;
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct)
    {
        Calls.Add("close");
        Closed = true;
        return Task.CompletedTask;
    }

    public Task SetQueueAsync(string itemId, double startSeconds, CancellationToken ct)
    {
        Calls.Add($"queue {itemId} {startSeconds}");
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct)
    {
        Calls.Add("pause");
        return Task.CompletedTask;
    }

    public Task UnpauseAsync(CancellationToken ct)
    {
        Calls.Add("unpause");
        return Task.CompletedTask;
    }

    public Task SeekAsync(double seconds, CancellationToken ct)
    {
        Calls.Add($"seek {seconds}");
        return Task.CompletedTask;
    }
}

internal sealed class FakeLibrary : IMediaLibrary
{
    public bool HasUserCredentials { get; set; }

    public Dictionary<string, List<MediaItem>> Search { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<MediaItem>> Seasons { get; } = [];

    public Dictionary<string, List<MediaItem>> Episodes { get; } = [];

    public Dictionary<string, MediaItem> Items { get; } = [];

    public Exception? SearchError { get; set; }

    public bool NoPlayback { get; set; }

    public bool UnknownMetadata { get; set; }

    public ProbeResult? Probe { get; set; }

    public string Codec { get; set; } = "h264";

    /// <summary>Source frame rate PlaybackInfo reports (spec 007).</summary>
    public double? FrameRate { get; set; }

    /// <summary>Spec 006: sessions without subtitles come back as direct streams of the original file.</summary>
    public bool Direct { get; set; }

    public int Resolves { get; private set; }

    /// <summary>Playback URL for an item id (default: a Jellyfin-like URL carrying an API key).</summary>
    public Func<string, string> UrlFor { get; set; } = id => $"http://jf/videos/{id}/master.m3u8?api_key=SECRET";

    public List<string> Stopped { get; } = [];

    public List<FakeSyncPlay> Groups { get; } = [];

    public bool FailSyncOpen { get; set; }

    private int _sessions;

    public Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken ct) =>
        SearchError is { } e ? Task.FromException<IReadOnlyList<MediaItem>>(e)
            : Task.FromResult<IReadOnlyList<MediaItem>>(Search.TryGetValue(query, out var r) ? r : []);

    public Task<IReadOnlyList<MediaItem>> FindEpisodeAsync(string show, int season, int episode, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MediaItem>>(Search.TryGetValue($"{show} {season}x{episode}", out var r) ? r : []);

    public Task<IReadOnlyList<MediaItem>> GetSeasonsAsync(string seriesId, CancellationToken ct) =>
        seriesId == "bad" ? Task.FromException<IReadOnlyList<MediaItem>>(new JellyfinException("seasons down"))
            : Task.FromResult<IReadOnlyList<MediaItem>>(Seasons.TryGetValue(seriesId, out var r) ? r : []);

    public Task<IReadOnlyList<MediaItem>> GetEpisodesAsync(string seriesId, string seasonId, CancellationToken ct) =>
        seasonId == "bad" ? Task.FromException<IReadOnlyList<MediaItem>>(new JellyfinException("episodes down"))
            : Task.FromResult<IReadOnlyList<MediaItem>>(Episodes.TryGetValue(seasonId, out var r) ? r : []);

    public Task<MediaItem?> GetItemAsync(string itemId, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault(itemId));

    /// <summary>When set, StartPlaybackAsync waits for it (simulates a slow server mid-start).</summary>
    public TaskCompletionSource? PlaybackGate { get; set; }

    public TaskCompletionSource PlaybackEntered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Audio/subtitle tracks per item id (spec 005).</summary>
    public Dictionary<string, List<MediaTrack>> Tracks { get; } = [];

    /// <summary>Every StartPlaybackAsync call: (item, tracks requested).</summary>
    public List<(string Item, TrackSelection? Tracks)> PlaybackCalls { get; } = [];

    /// <summary>The allowDirect argument of each StartPlaybackAsync call, in order (spec 012).</summary>
    public List<bool> AllowDirectCalls { get; } = [];

    /// <summary>Spec 029: Jellyfin's own track pick per item id (default: the file's default audio, no subtitles).</summary>
    public Dictionary<string, TrackSelection> JellyfinDefaults { get; } = [];

    public Exception? DefaultTracksError { get; set; }

    public int DefaultTracksCalls { get; private set; }

    public Task<JellyfinTracks?> GetDefaultTracksAsync(string itemId, CancellationToken ct)
    {
        DefaultTracksCalls++;
        if (DefaultTracksError is { } error)
        {
            return Task.FromException<JellyfinTracks?>(error);
        }

        return Task.FromResult<JellyfinTracks?>(new JellyfinTracks(
            JellyfinDefaults.GetValueOrDefault(itemId, TrackSelection.Default),
            Tracks.TryGetValue(itemId, out var tracks) ? tracks : []));
    }

    public async Task<PlaybackStream?> StartPlaybackAsync(string itemId, TrackSelection? tracks, CancellationToken ct, bool allowDirect = true)
    {
        lock (PlaybackCalls)
        {
            PlaybackCalls.Add((itemId, tracks));
            AllowDirectCalls.Add(allowDirect);
        }

        PlaybackEntered.TrySetResult();
        if (PlaybackGate is { } gate)
        {
            await gate.Task;
        }

        var stream = await StartPlaybackCoreAsync(itemId);
        return stream is null ? null : stream with
        {
            Tracks = Tracks.TryGetValue(itemId, out var t) ? t : [],
            FrameRate = FrameRate,
            Selection = tracks ?? TrackSelection.Default,
            IsDirect = Direct && allowDirect && tracks?.SubtitleIndex is null,
            TranscodeReason = Direct && allowDirect && tracks?.SubtitleIndex is null ? null
                : !Direct ? null : tracks?.SubtitleIndex is not null ? "burning in subtitles" : "the original file failed to start",
            Url = Direct && allowDirect && tracks?.SubtitleIndex is null ? $"http://jf/Videos/{itemId}/stream?static=true&api_key=SECRET" : stream.Url,
        };
    }

    private Task<PlaybackStream?> StartPlaybackCoreAsync(string itemId)
    {
        if (NoPlayback)
        {
            return Task.FromResult<PlaybackStream?>(null);
        }

        var n = Interlocked.Increment(ref _sessions);
        return Task.FromResult<PlaybackStream?>(new PlaybackStream(
            UrlFor(itemId), $"ps{n}", UnknownMetadata ? null : true, UnknownMetadata ? null : Codec));
    }

    public Task StopEncodingAsync(string? playSessionId, CancellationToken ct)
    {
        lock (Stopped)
        {
            Stopped.Add(playSessionId ?? "");
        }

        return Task.CompletedTask;
    }

    public Task<string> ResolveVariantAsync(string url, CancellationToken ct)
    {
        Resolves++;
        return Task.FromResult(url.Replace("master", "variant", StringComparison.Ordinal));
    }

    public Task<ProbeResult?> ProbeAsync(string url, CancellationToken ct) => Task.FromResult(Probe);

    public ISyncPlay CreateSyncPlay(ulong guildId, Func<SyncPlayCommand, CancellationToken, Task> onCommand, Func<SyncPlayQueue, CancellationToken, Task> onPlayQueue)
    {
        var g = new FakeSyncPlay(onCommand, onPlayQueue) { FailOpen = FailSyncOpen };
        Groups.Add(g);
        return g;
    }
}

/// <summary>Router + media + voice modules over fakes. Friend is user 20, in voice channel 50 of guild 5.</summary>
internal sealed class MediaHarness
{
    public FakeSender Sender { get; } = new();

    public FakeVoiceGateway Gateway { get; } = new();

    public FakeConnector Connector { get; } = new();

    public FakePipelineFactory Pipelines { get; }

    public FakeLibrary Library { get; } = new();

    public ManualTimeProvider Time { get; } = new();

    public VoiceManager Voice { get; }

    public StreamEngine Engine { get; }

    public MediaModule Media { get; }

    public CommandRouter Router { get; }

    public MessageHub Hub { get; }

    /// <summary>Reply to a picker once it is actually waiting.</summary>
    public async Task Answer(string content)
    {
        await Until(() => Hub.WaiterCount > 0, "no prompt is waiting");
        await Send(content);
    }

    /// <summary>The voice commands registered with the media module (spec 023 tests watch its countdown).</summary>
    public VoiceModule VoiceCommands { get; }

    public MediaHarness(bool configured = true, TimeSpan? maxPlaytime = null, TimeSpan? emptyChannelTimeout = null, JellySend.Common.Config.SettingsStore? settings = null)
    {
        Pipelines = new FakePipelineFactory(Connector.Log);
        Voice = new VoiceManager(Gateway);
        Engine = new StreamEngine(Connector, Pipelines);
        Media = new MediaModule(configured ? Library : null, Engine, Voice, g => g == 5 ? "Movie Night" : null, Time)
        {
            MaxPlaytime = maxPlaytime ?? TimeSpan.FromHours(12),
            Settings = settings ?? new JellySend.Common.Config.SettingsStore(null),
            Announcer = Sender,
        };
        Hub = new MessageHub(Time);
        Router = new CommandRouter(new FakeRoles(), Sender, Hub);
        VoiceCommands = new VoiceModule(Voice, Media, time: Time, emptyChannelTimeout: emptyChannelTimeout, announcer: Sender);
        Media.Register(Router, VoiceCommands);
        Gateway.States[(5, 20)] = 50;
    }

    public IReadOnlyList<string> Texts
    {
        get
        {
            lock (Sender.Sent)
            {
                return [.. Sender.Texts];
            }
        }
    }

    public FakePipeline Current => Pipelines.Created[^1];

    public static MediaItem Movie(string id, string name, int? year = 2010, long? ticks = 60L * 60 * 10_000_000) =>
        new() { Id = id, Name = name, Type = "Movie", ProductionYear = year, RunTimeTicks = ticks };

    public static MediaItem Episode(string id, string series, int season, int episode, string name) =>
        new() { Id = id, Name = name, Type = "Episode", SeriesName = series, ParentIndexNumber = season, IndexNumber = episode };

    public Task Send(string content, ulong? guild = 5, ulong author = 20, ulong channel = 7) =>
        Router.DispatchAsync(new ChatMessage(guild, channel, author, content), TestContext.Current.CancellationToken);

    /// <summary>Send without awaiting (the handler waits for a picker reply).</summary>
    public Task Start(string content) => Task.Run(() => Send(content));

    /// <summary>
    /// <c>!playlist</c> on its own (spec 021): its reply without the menu at the end. The menu keeps waiting for a reply
    /// in the background.
    /// </summary>
    public async Task<string> ShowPlaylist(string command = "!playlist")
    {
        var before = Texts.Count;
        _ = Start(command);
        await Until(() => Texts.Skip(before).Any(t => t.Contains(MenuMarker, StringComparison.Ordinal)), "the playlist menu");
        var reply = Texts.Skip(before).First(t => t.Contains(MenuMarker, StringComparison.Ordinal));
        return reply[..reply.IndexOf("\n\n" + MenuMarker, StringComparison.Ordinal)];
    }

    public const string MenuMarker = "**Change it?**";

    public async Task Until(Func<bool> condition, string? what = null)
    {
        for (var i = 0; i < 600 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), what ?? $"timed out; replies: {string.Join(" | ", Texts)}");
    }

    public Task UntilReply(string text) => Until(() => Texts.Contains(text), $"no reply '{text}'; got: {string.Join(" | ", Texts)}");

    public async Task Drain()
    {
        await Until(() => Media.Background.IsEmpty);
    }
}

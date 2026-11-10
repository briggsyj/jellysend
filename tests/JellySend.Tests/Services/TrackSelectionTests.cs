using System.Text.Json;
using JellySend.Services.Jellyfin;

namespace JellySend.Tests.Services;

/// <summary>Spec 005: subtitle and audio track selection in the Jellyfin client.</summary>
public sealed class TrackSelectionTests
{
    private const string Base = "http://jf:8096";
    private readonly FakeHttpHandler _http = new();
    private readonly JellyfinClient _client;

    public TrackSelectionTests()
    {
        _client = new JellyfinClient(Base, apiKey: "testkey", http: new HttpClient(_http)) { CachedUserId = "u" };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Source(string url) => new
    {
        TranscodingUrl = url,
        MediaStreams = new object[]
        {
            new { Type = "Video", Index = 0, Codec = "hevc" },
            new { Type = "Audio", Index = 1, Codec = "eac3", Language = "eng", DisplayTitle = "English - Dolby Digital+ - 5.1 - Default", IsDefault = true },
            new { Type = "Audio", Index = 2, Codec = "aac", Language = "jpn" },
            new { Type = "Subtitle", Index = 3, Codec = "subrip", Language = "eng", DisplayTitle = "English - SRT" },
            new { Type = "Subtitle", Index = 4, Codec = "PGSSUB", Language = "eng", IsForced = true },
            new { Type = "Subtitle", Codec = "srt" },  // no index: skipped
            new { Type = "Data", Index = 6 },
        },
    };

    [Fact]
    public async Task Tracks_are_parsed_from_playback_info()
    {
        _http.Responder = _ => FakeHttpHandler.Json(new { MediaSources = new[] { Source("/t.m3u8") }, PlaySessionId = "s" });
        var stream = await _client.StartPlaybackAsync("i", null, Ct);
        Assert.Equal(
            [
                new MediaTrack(1, true, "eng", "English - Dolby Digital+ - 5.1 - Default", true, false, EmbeddedOrdinal: 0),
                new MediaTrack(2, true, "jpn", "jpn — AAC", false, false, EmbeddedOrdinal: 1),
                new MediaTrack(3, false, "eng", "English - SRT", false, false, EmbeddedOrdinal: 0),
                new MediaTrack(4, false, "eng", "eng — PGSSUB", false, true, EmbeddedOrdinal: 1),
            ],
            stream!.Tracks);
        Assert.Equal(TrackSelection.Default, stream.Selection);
    }

    [Fact]
    public async Task Default_selection_requests_no_subtitles()
    {
        _http.Responder = _ => FakeHttpHandler.Json(new { MediaSources = new[] { Source("/t.m3u8?SubtitleStreamIndex=3") } });
        var stream = await _client.StartPlaybackAsync("i", null, Ct);
        var body = _http.Requests[^1].Json;
        Assert.Equal(-1, body.GetProperty("SubtitleStreamIndex").GetInt32());
        Assert.False(body.TryGetProperty("AudioStreamIndex", out _));
        Assert.Equal(0, body.GetProperty("DeviceProfile").GetProperty("SubtitleProfiles").GetArrayLength());
        Assert.Contains("SubtitleStreamIndex=-1", stream!.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selected_tracks_burn_in_subtitles_and_pick_audio()
    {
        _http.Responder = _ => FakeHttpHandler.Json(new { MediaSources = new[] { Source("/t.m3u8?SubtitleStreamIndex=-1&AudioStreamIndex=1") } });
        var stream = await _client.StartPlaybackAsync("i", new TrackSelection(2, 3), Ct);
        var body = _http.Requests[^1].Json;
        Assert.Equal(3, body.GetProperty("SubtitleStreamIndex").GetInt32());
        Assert.Equal(2, body.GetProperty("AudioStreamIndex").GetInt32());
        var profiles = body.GetProperty("DeviceProfile").GetProperty("SubtitleProfiles").EnumerateArray().ToList();
        Assert.All(profiles, p => Assert.Equal("Encode", p.GetProperty("Method").GetString()));
        Assert.Contains(profiles, p => p.GetProperty("Format").GetString() == "pgssub");
        Assert.Contains(profiles, p => p.GetProperty("Format").GetString() == "srt");
        Assert.Contains("SubtitleStreamIndex=3", stream!.Url, StringComparison.Ordinal);
        Assert.Contains("AudioStreamIndex=2", stream.Url, StringComparison.Ordinal);
        Assert.Equal(new TrackSelection(2, 3), stream.Selection);
    }

    [Fact]
    public void Parse_tracks_tolerates_missing_streams() =>
        Assert.Empty(JellyfinClient.ParseTracks(JsonDocument.Parse("""{"Id":"x"}""").RootElement));
}

/// <summary>Spec 006: direct stream of the original file.</summary>
public sealed class DirectStreamTests
{
    private const string Base = "http://jf:8096";
    private readonly FakeHttpHandler _http = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private JellyfinClient Client(bool direct = true) =>
        new(Base, apiKey: "testkey", http: new HttpClient(_http)) { CachedUserId = "u", PreferDirectStream = direct };

    private void Respond(object source) =>
        _http.Responder = _ => FakeHttpHandler.Json(new { MediaSources = new[] { source }, PlaySessionId = "s" });

    private static object FileSource(string protocol = "File", string? videoType = "VideoFile", string? iso = null) => new
    {
        Id = "src 1",
        Protocol = protocol,
        VideoType = videoType,
        IsoType = iso,
        TranscodingUrl = "/t.m3u8",
        RunTimeTicks = 600_000_000L,
        MediaStreams = new object[]
        {
            new { Type = "Video", Index = 0, Codec = "HEVC" },
            new { Type = "Audio", Index = 1, Codec = "truehd", Language = "eng", IsDefault = true },
            new { Type = "Subtitle", Index = 2, Codec = "pgssub", Language = "eng" },
        },
    };

    [Fact]
    public async Task Plain_files_come_back_as_the_static_original()
    {
        Respond(FileSource());
        var s = await Client().StartPlaybackAsync("item 1", null, Ct);
        Assert.True(s!.IsDirect);
        Assert.Equal($"{Base}/Videos/item%201/stream?static=true&MediaSourceId=src+1&api_key=testkey", s.Url);
        Assert.Equal(("hevc", (bool?)true, 60.0), (s.VideoCodec, s.HasAudio, s.RuntimeSeconds));
        Assert.Equal(2, s.Tracks.Count);
    }

    [Fact]
    public async Task Audio_selection_stays_direct()
    {
        Respond(FileSource());
        var s = await Client().StartPlaybackAsync("i", new TrackSelection(1, null), Ct);
        Assert.True(s!.IsDirect);
        Assert.Equal(new TrackSelection(1, null), s.Selection);
    }

    [Theory]
    [InlineData(true, "File", "VideoFile", null, true)]
    [InlineData(true, "File", "BluRay", null, false)]
    [InlineData(true, "File", "VideoFile", "BluRay", false)]
    [InlineData(true, "Http", "VideoFile", null, false)]
    [InlineData(false, "File", "VideoFile", null, false)]
    public async Task Falls_back_to_jellyfins_transcode_when_not_a_plain_file_or_disabled(bool prefer, string protocol, string videoType, string? iso, bool direct)
    {
        Respond(FileSource(protocol, videoType, iso));
        var s = await Client(prefer).StartPlaybackAsync("i", null, Ct);
        Assert.Equal(direct, s!.IsDirect);
        if (!direct)
        {
            Assert.StartsWith($"{Base}/t.m3u8", s.Url, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Subtitles_always_use_jellyfins_burn_in_transcode()
    {
        Respond(FileSource());
        var s = await Client().StartPlaybackAsync("i", new TrackSelection(null, 2), Ct);
        Assert.False(s!.IsDirect);
        Assert.Contains("SubtitleStreamIndex=2", s.Url, StringComparison.Ordinal);
    }

    // ── spec 011: fall back to Jellyfin's transcode when the bot's FFmpeg cannot decode the original ──

    [Fact]
    public async Task Undecodable_originals_use_jellyfins_transcode()
    {
        Respond(FileSource());
        var formats = new List<DirectStreamFormat>();
        var client = Client();
        client.CanDecode = f =>
        {
            formats.Add(f);
            return f.VideoCodec != "hevc";
        };
        var s = await client.StartPlaybackAsync("i", null, Ct);
        Assert.False(s!.IsDirect);
        Assert.Contains("/t.m3u8", s.Url, StringComparison.Ordinal);
        Assert.Equal([new DirectStreamFormat("hevc", null, false, "truehd")], formats);
    }

    [Fact]
    public async Task Disallowing_direct_forces_jellyfins_transcode()
    {
        Respond(FileSource());
        var s = await Client().StartPlaybackAsync("i", null, Ct, allowDirect: false);
        Assert.False(s!.IsDirect);
        Assert.Contains("/t.m3u8", s.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decodable_originals_stay_direct()
    {
        Respond(FileSource());
        var client = Client();
        client.CanDecode = _ => true;
        Assert.True((await client.StartPlaybackAsync("i", null, Ct))!.IsDirect);
    }

    [Fact]
    public void Format_describes_the_video_and_the_audio_that_would_play()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"MediaStreams": [
              {"Type": "Video", "Index": 0, "Codec": "H264", "BitDepth": 10, "IsInterlaced": true},
              {"Type": "Audio", "Index": 1, "Codec": "aac"},
              {"Type": "Audio", "Index": 2, "Codec": "EAC3", "IsDefault": true},
              {"Type": "Audio", "Index": 3, "Codec": "ac3"}
            ]}
            """);
        var source = doc.RootElement;
        Assert.Equal(new DirectStreamFormat("h264", 10, true, "eac3"), JellyfinClient.FormatOf(source, null)); // default track
        Assert.Equal(new DirectStreamFormat("h264", 10, true, "ac3"), JellyfinClient.FormatOf(source, 3));      // chosen track
        Assert.Equal("video h264 10-bit interlaced, audio eac3", JellyfinClient.FormatOf(source, 9).ToString());
    }

    [Theory]
    [InlineData("""{"MediaStreams": [{"Type": "Audio", "Codec": "flac"}, {"Type": "Audio", "Codec": "aac"}]}""", null, "flac")]
    [InlineData("""{"MediaStreams": [{"Type": "Video", "Codec": "av1"}]}""", "av1", null)]
    [InlineData("""{"MediaStreams": "x"}""", null, null)]
    [InlineData("""[]""", null, null)]
    public void Format_handles_missing_streams(string json, string? video, string? audio)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var f = JellyfinClient.FormatOf(doc.RootElement, null);
        Assert.Equal((video, audio), (f.VideoCodec, f.AudioCodec));
        Assert.Equal("video ?, audio none", new DirectStreamFormat(null, null, false, null).ToString());
    }

    // ── spec 013: Jellyfin indices vs the file's streams ──

    /// <summary>The production case: two external subtitle files listed first, so the AC-3 audio is Jellyfin index 3.</summary>
    private static object ExternalFirstSource(bool externalAudio = false) => new
    {
        Id = "src",
        Protocol = "File",
        VideoType = "VideoFile",
        TranscodingUrl = "/t.m3u8",
        MediaStreams = new object[]
        {
            new { Type = "Subtitle", Index = 0, Codec = "subrip", Language = "eng", IsExternal = true },
            new { Type = "Subtitle", Index = 1, Codec = "subrip", Language = "fre", IsExternal = true },
            new { Type = "Video", Index = 2, Codec = "h264" },
            new { Type = "Audio", Index = 3, Codec = "ac3", Language = "eng", IsDefault = true },
            new { Type = "Audio", Index = 4, Codec = "aac", Language = "eng", DisplayTitle = "Commentary.mka", IsExternal = externalAudio },
        },
    };

    [Fact]
    public async Task Tracks_know_their_position_in_the_file()
    {
        Respond(ExternalFirstSource());
        var s = await Client().StartPlaybackAsync("i", null, Ct);
        Assert.True(s!.IsDirect);
        var audio = s.Tracks.Where(t => t.IsAudio).ToList();
        Assert.Equal([(3, 0), (4, 1)], audio.Select(t => (t.Index, t.EmbeddedOrdinal!.Value)));
        Assert.All(s.Tracks.Where(t => !t.IsAudio), t => Assert.True(t.IsExternal));
        Assert.All(s.Tracks.Where(t => !t.IsAudio), t => Assert.Null(t.EmbeddedOrdinal));
        Assert.Equal(0, JellyfinClient.PlayingAudio(s.Tracks, null)!.EmbeddedOrdinal); // the default: 0:a:0, not 0:3
    }

    [Fact]
    public async Task An_external_audio_file_needs_jellyfins_transcode()
    {
        Respond(ExternalFirstSource(externalAudio: true));
        var client = Client();
        Assert.True((await client.StartPlaybackAsync("i", null, Ct))!.IsDirect);            // default audio is embedded
        var s = await client.StartPlaybackAsync("i", new TrackSelection(4, null), Ct);       // the external file
        Assert.False(s!.IsDirect);
        var external = s.Tracks.Single(t => t.Index == 4);
        Assert.True(external.IsExternal);
        Assert.Null(external.EmbeddedOrdinal);
    }

    [Fact]
    public void Playing_audio_is_the_chosen_then_default_then_first_track()
    {
        MediaTrack[] tracks =
        [
            new(5, false, "eng", "Subs", true, false),
            new(6, true, "jpn", "Japanese", false, false, EmbeddedOrdinal: 0),
            new(7, true, "eng", "English", true, false, EmbeddedOrdinal: 1),
        ];
        Assert.Equal(6, JellyfinClient.PlayingAudio(tracks, 6)!.Index);
        Assert.Equal(7, JellyfinClient.PlayingAudio(tracks, null)!.Index);
        Assert.Equal(7, JellyfinClient.PlayingAudio(tracks, 99)!.Index);
        Assert.Equal(6, JellyfinClient.PlayingAudio([tracks[0], tracks[1]], null)!.Index);
        Assert.Null(JellyfinClient.PlayingAudio([tracks[0]], null));
    }

    // ── spec 017: why Jellyfin transcodes, for the stats ──

    [Fact]
    public async Task A_direct_stream_has_no_transcode_reason()
    {
        Respond(FileSource());
        Assert.Null((await Client().StartPlaybackAsync("i", null, Ct))!.TranscodeReason);
    }

    [Fact]
    public async Task Transcodes_say_why()
    {
        async Task<string?> Reason(JellyfinClient client, TrackSelection? tracks = null, bool allowDirect = true) =>
            (await client.StartPlaybackAsync("i", tracks, Ct, allowDirect))!.TranscodeReason;

        Respond(FileSource());
        Assert.Equal("direct stream turned off", await Reason(Client(direct: false)));
        Assert.Equal("the original file failed to start", await Reason(Client(), allowDirect: false));
        Assert.Equal("burning in subtitles", await Reason(Client(), new TrackSelection(null, 2)));
        var picky = Client();
        picky.CanDecode = _ => false;
        Assert.Equal("bot can't decode video hevc, audio truehd", await Reason(picky));

        Respond(FileSource(protocol: "Http"));
        Assert.Equal("not a plain file (disc image or remote source)", await Reason(Client()));

        Respond(ExternalFirstSource(externalAudio: true));
        Assert.Equal("external audio track", await Reason(Client(), new TrackSelection(4, null)));
    }
}

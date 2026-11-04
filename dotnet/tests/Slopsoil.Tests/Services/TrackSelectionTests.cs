using System.Text.Json;
using Slopsoil.Services.Jellyfin;

namespace Slopsoil.Tests.Services;

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
                new MediaTrack(1, true, "eng", "English - Dolby Digital+ - 5.1 - Default", true, false),
                new MediaTrack(2, true, "jpn", "jpn — AAC", false, false),
                new MediaTrack(3, false, "eng", "English - SRT", false, false),
                new MediaTrack(4, false, "eng", "eng — PGSSUB", false, true),
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

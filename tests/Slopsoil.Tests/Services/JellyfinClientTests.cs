using System.Net;
using Microsoft.Extensions.Logging;
using Slopsoil.Common.Config;
using Slopsoil.Services.Jellyfin;
using Slopsoil.Tests.Discord;

namespace Slopsoil.Tests.Services;

public sealed class JellyfinClientTests
{
    private const string Base = "http://jellyfin.local:8096";

    private readonly FakeHttpHandler _http = new();
    private readonly ListLogger _log = new();
    private readonly JellyfinClient _client;

    public JellyfinClientTests()
    {
        _client = new JellyfinClient(Base, apiKey: "testkey", http: _http.Client(), logger: _log);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private JellyfinClient LoginClient(string url = "http://jf") =>
        new(url, username: "bob", password: "pw", http: _http.Client(), logger: _log);

    private static HttpResponseMessage PlaybackResponse(object source, string? playSessionId = "sess-1") =>
        FakeHttpHandler.Json(playSessionId is null
            ? new { MediaSources = new[] { source } }
            : new { MediaSources = new[] { source }, PlaySessionId = playSessionId });

    private static HttpResponseMessage Items(params object[] items) => FakeHttpHandler.Json(new { Items = items });

    private static HttpResponseMessage AuthOk(string token = "T", string userId = "U") =>
        FakeHttpHandler.Json(new { AccessToken = token, User = new { Id = userId } });

    // ---- user id ----

    [Fact]
    public async Task Get_user_id_fetches_and_caches()
    {
        _http.Responder = _ => FakeHttpHandler.Json("""[{"Id": "user-abc", "Name": "Admin"}]""");
        Assert.Equal("user-abc", await _client.GetUserIdAsync(Ct));
        _http.Responder = _ => throw new InvalidOperationException("should not be called");
        Assert.Equal("user-abc", await _client.GetUserIdAsync(Ct));
        var request = Assert.Single(_http.Requests);
        Assert.Equal($"{Base}/Users", request.Url);
        Assert.EndsWith("Token=\"testkey\"", request.Authorization, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "[]")]
    [InlineData(HttpStatusCode.OK, "[]")]
    [InlineData(HttpStatusCode.OK, "[{\"Name\": \"no id\"}]")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task Get_user_id_returns_null_on_failure(HttpStatusCode status, string body)
    {
        _http.Responder = _ => FakeHttpHandler.Json(body, status);
        Assert.Null(await _client.GetUserIdAsync(Ct));
        Assert.Null(_client.CachedUserId);
    }

    [Fact]
    public async Task Get_user_id_failure_is_logged()
    {
        _http.Responder = _ => throw new HttpRequestException("403 Forbidden");
        Assert.Null(await _client.GetUserIdAsync(Ct));
        Assert.True(_log.Has(LogLevel.Warning, "could not fetch user list"));
    }

    // ---- PlaybackInfo ----

    [Fact]
    public async Task Start_playback_posts_playback_info_with_hls_profile()
    {
        _client.CachedUserId = "user-abc";
        _http.Responder = _ => PlaybackResponse(new { TranscodingUrl = "/Videos/item-1/main/stream.m3u8?DeviceId=slopsoil&PlaySessionId=abc" });

        var url = await _client.GetStreamUrlAsync("item-1", Ct);

        Assert.Equal($"{Base}/Videos/item-1/main/stream.m3u8?DeviceId=slopsoil&PlaySessionId=abc&SubtitleStreamIndex=-1&api_key=testkey", url);
        var request = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/Items/item-1/PlaybackInfo", request.Path);
        Assert.Equal(new Dictionary<string, string> { ["DeviceId"] = AuthHeader.DefaultDeviceId, ["userId"] = "user-abc" }, request.Query);
        Assert.EndsWith("Token=\"testkey\"", request.Authorization, StringComparison.Ordinal);
        const string expected = """
            {"SubtitleStreamIndex":-1,"DeviceProfile":{"MaxStreamingBitrate":8000000,
            "TranscodingProfiles":[{"Container":"ts","Type":"Video","Protocol":"hls","AudioCodec":"aac","VideoCodec":"h264","MaxAudioChannels":2,"Context":"Streaming"}],
            "DirectPlayProfiles":[],"CodecProfiles":[],"SubtitleProfiles":[]},"UserId":"user-abc"}
            """;
        Assert.Equal(expected.Replace("\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal), request.Body);
    }

    [Fact]
    public async Task Start_playback_without_user_id_omits_it()
    {
        _http.Responder = r => r.Path == "/Users"
            ? FakeHttpHandler.Json("[]")
            : PlaybackResponse(new { TranscodingUrl = "/t.m3u8" });

        var stream = await _client.StartPlaybackAsync("item-1", null, Ct);

        Assert.NotNull(stream);
        Assert.Equal($"{Base}/t.m3u8?SubtitleStreamIndex=-1&api_key=testkey", stream.Url);
        var request = _http.Requests[^1];
        Assert.Equal(new Dictionary<string, string> { ["DeviceId"] = AuthHeader.DefaultDeviceId }, request.Query);
        Assert.False(request.Json.TryGetProperty("UserId", out _));
    }

    [Fact]
    public async Task Stream_url_overrides_subtitle_index()
    {
        _client.CachedUserId = "user-abc";
        _http.Responder = _ => PlaybackResponse(new { TranscodingUrl = "/Videos/item-1/stream.m3u8?SubtitleStreamIndex=0&PlaySessionId=abc" });
        var url = await _client.GetStreamUrlAsync("item-1", Ct);
        Assert.Equal($"{Base}/Videos/item-1/stream.m3u8?SubtitleStreamIndex=-1&PlaySessionId=abc&api_key=testkey", url);
    }

    [Theory]
    [InlineData("/s?a=1&a=2&flag&b=x+y%2Cz", "/s?a=1&flag=&b=x+y%2Cz&SubtitleStreamIndex=-1")]
    [InlineData("/s?&&b=1#frag", "/s?b=1&SubtitleStreamIndex=-1#frag")]
    [InlineData("/s", "/s?SubtitleStreamIndex=-1")]
    public void Subtitle_rewrite_matches_parse_qs_urlencode(string input, string expected)
    {
        Assert.Equal(expected, JellyfinClient.ForceNoSubtitles(input, out var session));
        Assert.Null(session);
    }

    [Fact]
    public async Task Stream_url_falls_back_to_direct_stream_and_keeps_absolute_urls()
    {
        _client.CachedUserId = "u";
        _http.Responder = _ => PlaybackResponse(new { DirectStreamUrl = "https://cdn.example/v/stream?Container=mp4" });
        var url = await _client.GetStreamUrlAsync("item-1", Ct);
        Assert.Equal("https://cdn.example/v/stream?Container=mp4&SubtitleStreamIndex=-1&api_key=testkey", url);
    }

    [Fact]
    public async Task Play_session_falls_back_to_url_query()
    {
        _client.CachedUserId = "u";
        _http.Responder = _ => PlaybackResponse(new { TranscodingUrl = "/t.m3u8?PlaySessionId=from-url" }, playSessionId: null);
        var stream = await _client.StartPlaybackAsync("item-1", null, Ct);
        Assert.Equal("from-url", stream!.PlaySessionId);
    }

    [Theory]
    [InlineData("""{"MediaSources": []}""", "no media sources")]
    [InlineData("""{}""", "no media sources")]
    [InlineData("""[]""", "no media sources")]
    [InlineData("""{"MediaSources": [{"Id": "s", "TranscodingUrl": ""}]}""", "No stream URL")]
    public async Task Start_playback_returns_null_without_a_usable_source(string body, string logged)
    {
        _client.CachedUserId = "u";
        _http.Responder = _ => FakeHttpHandler.Json(body);
        Assert.Null(await _client.GetStreamUrlAsync("item-1", Ct));
        Assert.True(_log.Has(LogLevel.Warning, logged));
    }

    [Fact]
    public async Task Start_playback_returns_null_on_request_failure_with_redacted_log()
    {
        _client.CachedUserId = "u";
        _http.Responder = _ => throw new HttpRequestException("connection refused to http://jf/x?api_key=SECRET");
        Assert.Null(await _client.StartPlaybackAsync("item-1", null, Ct));
        Assert.True(_log.Has(LogLevel.Warning, "PlaybackInfo failed for item item-1"));
        Assert.DoesNotContain("SECRET", _log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_playback_http_error_returns_null()
    {
        _client.CachedUserId = "u";
        _http.Responder = _ => FakeHttpHandler.Json("{}", HttpStatusCode.BadRequest);
        Assert.Null(await _client.StartPlaybackAsync("item-1", null, Ct));
        Assert.True(_log.Has(LogLevel.Warning, "HTTP 400"));
    }

    [Theory]
    [InlineData("""{"MediaStreams": [{"Type": "Audio"}, {"Type": "Video", "AverageFrameRate": 23.976025, "RealFrameRate": 24}]}""", 23.976025)]
    [InlineData("""{"MediaStreams": [{"Type": "Video", "RealFrameRate": 25}]}""", 25.0)]
    [InlineData("""{"MediaStreams": [{"Type": "Video", "AverageFrameRate": 0, "RealFrameRate": 30}]}""", 30.0)]
    [InlineData("""{"MediaStreams": [{"Type": "Video"}, {"Type": "Video", "RealFrameRate": 25}]}""", null)]
    [InlineData("""{"MediaStreams": [1, {"Type": "Audio"}]}""", null)]
    [InlineData("""{"MediaStreams": "x"}""", null)]
    [InlineData("""{}""", null)]
    [InlineData("""[]""", null)]
    public void Frame_rate_comes_from_the_first_video_stream(string source, double? expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(source);
        Assert.Equal(expected, JellyfinClient.FrameRateOf(doc.RootElement));
    }

    public static TheoryData<string, bool?, string?> MetadataCases => new()
    {
        { """{"TranscodingUrl": "/t.m3u8?a=1", "MediaStreams": [{"Type": "Video"}, {"Type": "Audio"}]}""", true, "h264" },
        { """{"TranscodingUrl": "/t.m3u8?a=1", "MediaStreams": [{"Type": "Video", "Codec": "hevc"}]}""", false, "h264" },
        { """{"DirectStreamUrl": "/d?a=1", "MediaStreams": [{"Type": "Video", "Codec": "HEVC"}, {"Type": "Audio"}]}""", true, "hevc" },
        { """{"DirectStreamUrl": "/d?a=1", "MediaStreams": [{"Type": "Video", "Codec": 5}, {"Type": "Audio"}]}""", true, null },
        { """{"DirectStreamUrl": "/d?a=1", "MediaStreams": [{"Type": "Audio"}]}""", true, null },
        { """{"TranscodingUrl": "/t.m3u8?a=1"}""", null, null },
        { """{"TranscodingUrl": "/t.m3u8?a=1", "MediaStreams": []}""", null, null },
        { """{"TranscodingUrl": "/t.m3u8?a=1", "MediaStreams": "x"}""", null, null },
        { """{"TranscodingUrl": "/t.m3u8?a=1", "MediaStreams": [{"Type": "Audio"}]}""", true, null },
        { """{"TranscodingUrl": "/t.m3u8?a=1", "MediaStreams": [1, {"Type": "Subtitle"}]}""", false, null },
    };

    [Theory]
    [MemberData(nameof(MetadataCases))]
    public async Task Start_playback_reports_stream_metadata(string source, bool? hasAudio, string? codec)
    {
        _client.CachedUserId = "u";
        _http.Responder = _ => FakeHttpHandler.Json($$"""{"MediaSources": [{{source}}], "PlaySessionId": "ps-9"}""");
        var stream = await _client.StartPlaybackAsync("item-1", null, Ct);
        Assert.NotNull(stream);
        Assert.Equal((hasAudio, codec), (stream.HasAudio, stream.VideoCodec));
        Assert.Equal("ps-9", stream.PlaySessionId);
    }

    [Theory]
    [InlineData("36000000000", 3600.0)]
    [InlineData("0", null)]
    [InlineData("\"x\"", null)]
    public async Task Start_playback_reports_source_runtime(string ticks, double? seconds)
    {
        _client.CachedUserId = "u";
        _http.Responder = _ => FakeHttpHandler.Json($$"""{"MediaSources": [{"TranscodingUrl": "/t", "RunTimeTicks": {{ticks}}}]}""");
        var stream = await _client.StartPlaybackAsync("item-1", null, Ct);
        Assert.Equal(seconds, stream!.RuntimeSeconds);
        Assert.Null(stream.PlaySessionId);
    }

    // ---- search / seasons / episodes / find_episode ----

    [Fact]
    public async Task Search_returns_items_with_cog_fields()
    {
        _http.Responder = _ => FakeHttpHandler.Json("""
            {"Items": [
              {"Id": "1", "Name": "Sample Film", "Type": "Movie", "ProductionYear": 2010, "RunTimeTicks": 88800000000, "Extra": {"x": 1}},
              {"Id": "2", "Name": "Pilot", "Type": "Episode", "SeriesName": "Space Show", "SeriesId": "s", "SeasonId": "s1",
               "SeasonName": "Season 1", "ParentIndexNumber": 1, "IndexNumber": 1},
              {"Id": "3", "Name": "Space Show", "Type": "Series"}
            ], "TotalRecordCount": 3}
            """);

        var results = await _client.SearchAsync("cartoon show", ct: Ct);

        Assert.Equal(3, results.Count);
        Assert.Equal(new MediaItem { Id = "1", Name = "Sample Film", Type = "Movie", ProductionYear = 2010, RunTimeTicks = 88_800_000_000 }, results[0]);
        Assert.Equal(8880.0, results[0].RuntimeSeconds);
        Assert.False(results[0].IsSeries);
        Assert.Equal(
            new MediaItem
            {
                Id = "2", Name = "Pilot", Type = "Episode", SeriesName = "Space Show", SeriesId = "s", SeasonId = "s1",
                SeasonName = "Season 1", ParentIndexNumber = 1, IndexNumber = 1,
            },
            results[1]);
        Assert.Null(results[1].RuntimeSeconds);
        Assert.True(results[2].IsSeries);

        var request = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/Items", request.Path);
        Assert.Contains("searchTerm=cartoon+show&", request.Url, StringComparison.Ordinal);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["searchTerm"] = "cartoon show",
                ["Recursive"] = "true",
                ["Limit"] = "25",
                ["IncludeItemTypes"] = "Movie,Series,Episode",
                ["Fields"] = "ProductionYear,SeriesName,SeasonName,IndexNumber,ParentIndexNumber",
            },
            request.Query);
    }

    [Theory]
    [InlineData("""{"Items": []}""")]
    [InlineData("""{}""")]
    [InlineData("")]
    public async Task Search_empty_result(string body)
    {
        _http.Responder = _ => FakeHttpHandler.Json(body);
        Assert.Empty(await _client.SearchAsync("nothing", 5, Ct));
        Assert.Equal("5", _http.Requests[0].Query["Limit"]);
    }

    [Fact]
    public async Task Search_failures_throw_jellyfin_exception()
    {
        _http.Responder = _ => FakeHttpHandler.Json("{}", HttpStatusCode.InternalServerError);
        var ex = await Assert.ThrowsAsync<JellyfinException>(() => _client.SearchAsync("x", ct: Ct));
        Assert.Equal("Jellyfin GET /Items → HTTP 500", ex.Message);

        _http.Responder = _ => FakeHttpHandler.Json("""{"Items": [{"Id": 5}]}""");
        await Assert.ThrowsAsync<JellyfinException>(() => _client.SearchAsync("x", ct: Ct));

        _http.Responder = _ => FakeHttpHandler.Json("{oops");
        await Assert.ThrowsAsync<JellyfinException>(() => _client.SearchAsync("x", ct: Ct));

        _http.Responder = _ => throw new HttpRequestException("refused");
        await Assert.ThrowsAsync<JellyfinException>(() => _client.SearchAsync("x", ct: Ct));
    }

    [Fact]
    public async Task Request_timeout_throws_jellyfin_exception()
    {
        var time = new ManualTimeProvider();
        using var client = new JellyfinClient(Base, "k", http: _http.Client(), time: time);
        _http.AsyncResponder = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FakeHttpHandler.Json("{}");
        };

        var search = client.SearchAsync("x", ct: Ct);
        await time.NextTimerAsync();
        time.Advance(JellyfinClient.RequestTimeout);
        var ex = await Assert.ThrowsAsync<JellyfinException>(() => search);
        Assert.Equal("Jellyfin GET /Items timed out", ex.Message);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _http.AsyncResponder = async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return FakeHttpHandler.Json("{}");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.SearchAsync("x", ct: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.StopEncodingAsync("ps", cts.Token));
    }

    [Fact]
    public async Task Get_seasons_sorted_by_index_number()
    {
        _http.Responder = _ => Items(
            new { Id = "s2", Name = "Season 2", IndexNumber = 2 },
            new { Id = "s1", Name = "Season 1", IndexNumber = 1 },
            new { Id = "sp", Name = "Specials" },
            new { Id = "s3", Name = "Season 3", IndexNumber = 3 });

        var seasons = await _client.GetSeasonsAsync("series-abc", Ct);

        Assert.Equal(["sp", "s1", "s2", "s3"], seasons.Select(s => s.Id));
        var request = Assert.Single(_http.Requests);
        Assert.Equal("/Shows/series-abc/Seasons", request.Path);
        Assert.Equal(new Dictionary<string, string> { ["Fields"] = "Id,Name,IndexNumber" }, request.Query);
    }

    [Fact]
    public async Task Get_episodes_sorted_by_index_number()
    {
        _http.Responder = _ => Items(
            new { Id = "e3", Name = "C", IndexNumber = 3 },
            new { Id = "e1", Name = "A", IndexNumber = 1 },
            new { Id = "e2", Name = "B", IndexNumber = 2 });

        var episodes = await _client.GetEpisodesAsync("series-abc", "season-1", Ct);

        Assert.Equal([1, 2, 3], episodes.Select(e => e.IndexNumber));
        var request = Assert.Single(_http.Requests);
        Assert.Equal("/Shows/series-abc/Episodes", request.Path);
        Assert.Equal(
            new Dictionary<string, string> { ["SeasonId"] = "season-1", ["Fields"] = "Id,Name,IndexNumber,ParentIndexNumber,SeriesName" },
            request.Query);
    }

    [Fact]
    public async Task Find_episode_resolves_via_series_seasons_episodes()
    {
        _http.Responder = r => r.Path switch
        {
            "/Items" => Items(
                new { Id = "movie-1", Name = "Cartoon Show: The Movie", Type = "Movie" },
                new { Id = "series-1", Name = "Cartoon Show", Type = "Series" },
                new { Id = "series-2", Name = "Cartoon Show (Anime)", Type = "Series" }),
            "/Shows/series-1/Seasons" => Items(new { Id = "s1", IndexNumber = 1 }, new { Id = "s2", IndexNumber = 2 }),
            "/Shows/series-1/Episodes" => Items(
                new { Id = "e1", Name = "Time Trouble", IndexNumber = 1, ParentIndexNumber = 2 },
                new { Id = "e2", Name = "Night Run", IndexNumber = 2, ParentIndexNumber = 2 }),
            "/Shows/series-2/Seasons" => Items(new { Id = "a1", IndexNumber = 1 }),
            _ => throw new InvalidOperationException(r.Url),
        };

        var results = await _client.FindEpisodeAsync("cartoon show", season: 2, episode: 1, Ct);

        Assert.Equal("e1", Assert.Single(results).Id);
        Assert.Equal("s2", _http.Requests.Single(r => r.Path == "/Shows/series-1/Episodes").Query["SeasonId"]);
        Assert.DoesNotContain(_http.Requests, r => r.Path.Contains("movie-1", StringComparison.Ordinal));
        Assert.DoesNotContain(_http.Requests, r => r.Path == "/Shows/series-2/Episodes");
    }

    [Fact]
    public async Task Find_episode_no_series_found()
    {
        _http.Responder = _ => Items(new { Id = "m", Type = "Movie" });
        Assert.Empty(await _client.FindEpisodeAsync("unknown show", 1, 1, Ct));
        Assert.Single(_http.Requests);
    }

    // ---- auth ----

    [Fact]
    public void Auth_header_with_and_without_token()
    {
        var header = AuthHeader.Build("tok", "dev-1");
        Assert.Equal("MediaBrowser Client=\"slopsoil\", Device=\"slopsoil\", DeviceId=\"dev-1\", Version=\"1.0\", Token=\"tok\"", header);
        var anon = AuthHeader.Build(null);
        Assert.DoesNotContain("Token=", anon, StringComparison.Ordinal);
        Assert.Contains($"DeviceId=\"{AuthHeader.DefaultDeviceId}\"", anon, StringComparison.Ordinal);
        Assert.Equal(anon, AuthHeader.Build(""));
    }

    [Fact]
    public async Task Authenticate_posts_credentials_for_device()
    {
        using var client = LoginClient();
        _http.Responder = _ => AuthOk("AT", "U1");

        Assert.Equal(new JellyfinSession("AT", "U1"), await client.AuthenticateAsync("slopsoil-5", Ct));

        var request = Assert.Single(_http.Requests);
        Assert.Equal("http://jf/Users/AuthenticateByName", request.Url);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("""{"Username":"bob","Pw":"pw"}""", request.Body);
        Assert.Equal(AuthHeader.Build(null, "slopsoil-5"), request.Authorization);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"AccessToken": "AT"}""")]
    [InlineData("""{"AccessToken": 1, "User": {"Id": "U"}}""")]
    public async Task Authenticate_unexpected_response_throws(string body)
    {
        using var client = LoginClient();
        _http.Responder = _ => FakeHttpHandler.Json(body);
        await Assert.ThrowsAsync<JellyfinException>(() => client.AuthenticateAsync(ct: Ct));
    }

    [Fact]
    public void Client_trims_base_url_and_uses_api_key()
    {
        using var client = new JellyfinClient("http://jf/", apiKey: "KEY");
        Assert.Equal("http://jf", client.BaseUrl);
        Assert.Equal("KEY", client.Token);
        Assert.False(client.HasUserCredentials);
        Assert.Equal("", client.Username);
    }

    [Theory]
    [InlineData("bob", "pw", true)]
    [InlineData("bob", "", false)]
    [InlineData("", "pw", false)]
    [InlineData(null, null, false)]
    public void Has_user_credentials(string? username, string? password, bool expected)
    {
        using var client = new JellyfinClient("http://jf", username: username, password: password);
        Assert.Equal(expected, client.HasUserCredentials);
    }

    [Fact]
    public void From_options_builds_only_when_configured()
    {
        Assert.Null(JellyfinClient.FromOptions(new SlopsoilOptions()));
        Assert.Null(JellyfinClient.FromOptions(new SlopsoilOptions { JellyfinUrl = "http://jf", JellyfinUsername = "bob" }));
        using var keyed = JellyfinClient.FromOptions(new SlopsoilOptions { JellyfinUrl = "http://jf", JellyfinApiKey = "K" })!;
        Assert.Equal("K", keyed.Token);
        Assert.False(keyed.HasUserCredentials);
        using var login = JellyfinClient.FromOptions(
            new SlopsoilOptions { JellyfinUrl = "http://jf", JellyfinUsername = "bob", JellyfinPassword = "pw" })!;
        Assert.True(login.HasUserCredentials);
        Assert.Equal("bob", login.Username);
    }

    [Fact]
    public async Task Lazy_sign_in_happens_once_under_a_lock()
    {
        using var client = LoginClient();
        using var gate = new SemaphoreSlim(0);
        var auths = 0;
        _http.AsyncResponder = async (r, ct) =>
        {
            if (r.Path == "/Users/AuthenticateByName")
            {
                Interlocked.Increment(ref auths);
                await gate.WaitAsync(ct);
                return AuthOk("T", "U");
            }

            return Items();
        };

        var first = client.SearchAsync("a", ct: Ct);
        var second = client.SearchAsync("b", ct: Ct);
        Assert.Equal("", client.Token);
        gate.Release();
        await Task.WhenAll(first, second);
        await client.GetSeasonsAsync("s", Ct);

        Assert.Equal(1, auths);
        Assert.Equal("T", client.Token);
        Assert.Equal("U", client.CachedUserId);
        Assert.All(_http.Requests.Skip(1), r => Assert.EndsWith("Token=\"T\"", r.Authorization, StringComparison.Ordinal));
        Assert.True(_log.Has(LogLevel.Information, "signed in as bob"));
    }

    [Fact]
    public async Task Lazy_sign_in_failure_propagates_and_is_retried()
    {
        using var client = LoginClient();
        _http.Responder = _ => FakeHttpHandler.Json("{}", HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<JellyfinException>(() => client.SearchAsync("x", ct: Ct));
        _http.Responder = r => r.Path == "/Users/AuthenticateByName" ? AuthOk() : Items();
        await client.SearchAsync("x", ct: Ct);
        Assert.Equal("T", client.Token);
    }

    [Fact]
    public async Task No_sign_in_with_api_key_or_without_credentials()
    {
        using var keyed = new JellyfinClient("http://jf", apiKey: "K", username: "bob", password: "pw", http: _http.Client());
        using var anonymous = new JellyfinClient("http://jf", http: _http.Client());
        await keyed.EnsureAuthAsync(Ct);
        await anonymous.EnsureAuthAsync(Ct);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Signed_in_user_id_is_used_without_listing_users()
    {
        using var client = LoginClient();
        _http.Responder = _ => AuthOk("T", "U9");
        Assert.Equal("U9", await client.GetUserIdAsync(Ct));
        Assert.Equal("/Users/AuthenticateByName", Assert.Single(_http.Requests).Path);
    }

    // ---- get_item / stop_encoding ----

    [Fact]
    public async Task Get_item_found()
    {
        _http.Responder = _ => Items(new { Id = "abc", Name = "X" });
        Assert.Equal(new MediaItem { Id = "abc", Name = "X" }, await _client.GetItemAsync("abc", Ct));
        var request = Assert.Single(_http.Requests);
        Assert.StartsWith($"{Base}/Items?", request.Url, StringComparison.Ordinal);
        Assert.Equal("abc", request.Query["Ids"]);
        Assert.Equal("ProductionYear,SeriesName,IndexNumber,ParentIndexNumber", request.Query["Fields"]);
    }

    [Fact]
    public async Task Get_item_missing()
    {
        _http.Responder = _ => Items();
        Assert.Null(await _client.GetItemAsync("abc", Ct));
    }

    [Fact]
    public async Task Get_item_error_returns_null_and_logs()
    {
        _http.Responder = _ => throw new HttpRequestException("down");
        Assert.Null(await _client.GetItemAsync("abc", Ct));
        Assert.True(_log.Has(LogLevel.Warning, "could not fetch item abc"));
    }

    [Fact]
    public async Task Stop_encoding_deletes_active_encoding()
    {
        _http.Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        await _client.StopEncodingAsync("ps-1", Ct);
        var request = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"{Base}/Videos/ActiveEncodings?deviceId={AuthHeader.DefaultDeviceId}&playSessionId=ps-1", request.Url);
        Assert.EndsWith("Token=\"testkey\"", request.Authorization, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Stop_encoding_without_session_is_noop(string? session)
    {
        await _client.StopEncodingAsync(session, Ct);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Stop_encoding_errors_are_swallowed()
    {
        _http.Responder = _ => FakeHttpHandler.Json("{}", HttpStatusCode.NotFound);
        await _client.StopEncodingAsync("ps-1", Ct);
        _http.Responder = _ => throw new HttpRequestException("refused");
        await _client.StopEncodingAsync("ps-1", Ct);
        Assert.True(_log.Has(LogLevel.Debug, "stop_encoding(ps-1) failed"));
    }

    [Fact]
    public void Ticks_round_trip()
    {
        Assert.Equal(15_000_000, JellyfinTicks.FromSeconds(1.5));
        Assert.Equal(0, JellyfinTicks.FromSeconds(0.00000004));
        Assert.Equal(1.5, JellyfinTicks.ToSeconds(15_000_000));
    }

    [Fact]
    public void Jellyfin_exception_keeps_inner()
    {
        var inner = new IOException("x");
        Assert.Same(inner, new JellyfinException("m", inner).InnerException);
    }
}

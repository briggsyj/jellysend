using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using JellySend.Services.Media;
using JellySend.Tests.Discord;

namespace JellySend.Tests.Services;

public sealed class HlsVariantResolverTests
{
    private const string Master = "http://tv.example/live/master.m3u8?token=SECRET";

    private const string MasterPlaylist = """
        #EXTM3U
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",URI="mono.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,AUDIO="aud"
        low/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720
        tracks-v1a1/mono.m3u8?token=SECRET
        #EXT-X-STREAM-INF:BANDWIDTH=1200000
        mid.m3u8
        """;

    private readonly FakeHttpHandler _http = new();
    private readonly ListLogger _log = new();
    private readonly HlsVariantResolver _resolver;

    public HlsVariantResolverTests()
    {
        _resolver = new HlsVariantResolver(_http.Client(), _log);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.apple.mpegurl") };

    [Fact]
    public async Task Master_playlist_resolves_to_highest_bandwidth_variant()
    {
        _http.Responder = _ => Text(MasterPlaylist);

        var url = await _resolver.ExtractVariantUrlAsync(Master, Ct);

        Assert.Equal("http://tv.example/live/tracks-v1a1/mono.m3u8?token=SECRET", url);
        var request = Assert.Single(_http.Requests);
        Assert.Equal(Master, request.Url);
        Assert.True(_log.Has(LogLevel.Information, "HLS master → variant"));
        Assert.DoesNotContain("SECRET", _log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_sends_user_agent()
    {
        HttpRequestMessage? seen = null;
        var handler = new CapturingHandler(r => seen = r);
        await new HlsVariantResolver(new HttpClient(handler)).ExtractVariantUrlAsync("http://x/a.m3u8", Ct);
        Assert.Equal("jellysend/1.0", seen!.Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData("#EXTM3U\n#EXTINF:6,\nseg1.ts\n")] // media playlist
    [InlineData("--myboundary\r\nContent-Type: image/jpeg\r\n")] // not HLS
    [InlineData("#EXT-X-STREAM-INF:BANDWIDTH=1\nv.m3u8\n")] // no #EXTM3U
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n#comment\n")] // no URI after the tag
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n")] // tag on the last line
    public async Task Non_master_playlists_return_url_unchanged(string body)
    {
        _http.Responder = _ => Text(body);
        Assert.Equal(Master, await _resolver.ExtractVariantUrlAsync(Master, Ct));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task Fetch_errors_return_url_unchanged()
    {
        _http.Responder = _ => Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv.m3u8", HttpStatusCode.InternalServerError);
        Assert.Equal(Master, await _resolver.ExtractVariantUrlAsync(Master, Ct));
        _http.Responder = _ => throw new HttpRequestException("refused");
        Assert.Equal(Master, await _resolver.ExtractVariantUrlAsync(Master, Ct));
        Assert.Equal("not a url", await _resolver.ExtractVariantUrlAsync("not a url", Ct));
    }

    [Fact]
    public async Task Timeout_returns_url_unchanged()
    {
        var time = new ManualTimeProvider();
        var resolver = new HlsVariantResolver(_http.Client(), time: time);
        _http.AsyncResponder = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Text("");
        };
        var task = resolver.ExtractVariantUrlAsync(Master, Ct);
        await time.NextTimerAsync();
        time.Advance(HlsVariantResolver.Timeout);
        Assert.Equal(Master, await task);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _resolver.ExtractVariantUrlAsync(Master, cts.Token));
    }

    [Fact]
    public async Task Only_the_first_8_kib_are_read()
    {
        var head = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5\nfirst.m3u8\n";
        var padding = new string('#', HlsVariantResolver.MaxPeekBytes - head.Length);
        _http.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TrickleStream(Encoding.UTF8.GetBytes(head + padding + "\n#EXT-X-STREAM-INF:BANDWIDTH=9\nlate.m3u8\n"))),
        };
        Assert.Equal("http://tv.example/live/first.m3u8", await _resolver.ExtractVariantUrlAsync(Master, Ct));
    }

    [Theory]
    [InlineData("#EXT-X-STREAM-INF:BANDWIDTH=10\na.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=10\nb.m3u8", "http://h/p/a.m3u8")] // first wins ties
    [InlineData("#EXT-X-STREAM-INF:RESOLUTION=1x1\na.m3u8\n#EXT-X-STREAM-INF:bandwidth=3\nb.m3u8", "http://h/p/b.m3u8")] // missing = 0, case-insensitive
    [InlineData("  #EXT-X-STREAM-INF:BANDWIDTH=1  \r\n  https://cdn/v.m3u8  \r\n", "https://cdn/v.m3u8")] // absolute, trimmed, CRLF
    [InlineData("#EXT-X-STREAM-INF:BANDWIDTH=1\n\n#EXT-X-STREAM-INF:BANDWIDTH=0\nz.m3u8", "http://h/p/z.m3u8")]
    [InlineData("#EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=9,BANDWIDTH=1\na.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=5\nb.m3u8", "http://h/p/a.m3u8")] // re.search semantics
    public void Pick_variant(string body, string expected) =>
        Assert.Equal(expected, HlsVariantResolver.PickVariant("http://h/p/master.m3u8", "#EXTM3U\n" + body));

    [Fact]
    public async Task Variant_equal_to_master_is_not_logged()
    {
        _http.Responder = _ => Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nhttp://h/m.m3u8\n");
        Assert.Equal("http://h/m.m3u8", await _resolver.ExtractVariantUrlAsync("http://h/m.m3u8", Ct));
        Assert.Empty(_log.Entries);
        Assert.Equal("nopath/v.m3u8", HlsVariantResolver.PickVariant("nopath", "#EXTM3U\n#EXT-X-STREAM-INF:\nv.m3u8"));
    }

    [Fact]
    public void Default_constructor_uses_shared_client() => Assert.NotNull(new HlsVariantResolver());

    private sealed class CapturingHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            capture(request);
            return Task.FromResult(Text(""));
        }
    }

    /// <summary>Returns at most 100 bytes per read so the peek loop has to iterate.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 100));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 100)], cancellationToken);
    }
}

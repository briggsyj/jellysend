using System.ComponentModel;
using Microsoft.Extensions.Logging;
using JellySend.Services.Media;

namespace JellySend.Tests.Services;

public sealed class ProbeServiceTests
{
    private const string Url = "http://jf/Videos/1/main.m3u8?SubtitleStreamIndex=-1&api_key=SECRET";

    private const string Streams = """
        {"streams": [
          {"index": 0, "codec_type": "video", "codec_name": "h264", "profile": "High", "r_frame_rate": "30000/1001",
           "has_b_frames": 2, "width": 1920, "height": 1080},
          {"index": 1, "codec_type": "audio", "codec_name": "aac"},
          {"index": 2, "codec_type": "audio", "codec_name": "ac3"}
        ]}
        """;

    private readonly FakeProcessRunner _runner = new();
    private readonly ListLogger _log = new();
    private readonly ProbeService _probe;

    public ProbeServiceTests()
    {
        _probe = new ProbeService(_runner, _log, "/usr/bin/ffprobe");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Probe_runs_ffprobe_on_all_streams_and_summarises()
    {
        _runner.Result = () => new ProcessResult(0, Streams, "");

        var info = await _probe.ProbeStreamAsync(Url, Ct);

        Assert.Equal(new ProbeResult("h264", 29.97, "High", true, 1920, 1080, true, "aac"), info);
        var (file, args, timeout) = Assert.Single(_runner.Calls);
        Assert.Equal("/usr/bin/ffprobe", file);
        Assert.Equal(
            ["-v", "warning", "-show_streams", "-print_format", "json", "-probesize", "10000000", "-analyzeduration", "10000000", Url],
            args);
        Assert.Equal(TimeSpan.FromSeconds(30), timeout);
    }

    [Fact]
    public void Defaults_for_missing_fields_and_no_audio()
    {
        var info = ProbeService.Parse("""{"streams": [{"codec_type": "video", "has_b_frames": 0}, "junk"]}""");
        Assert.Equal(new ProbeResult("unknown", 25.0, "unknown", false, 0, 0, false, null), info);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"streams": {}}""")]
    [InlineData("""{"streams": [{"codec_type": "audio", "codec_name": "aac"}]}""")]
    public void No_video_or_bad_json_is_null(string json) => Assert.Null(ProbeService.Parse(json));

    [Theory]
    [InlineData("25/1", 25.0)]
    [InlineData("24000/1001", 23.976023976023978)]
    [InlineData("30", 25.0)]
    [InlineData("1/2/3", 25.0)]
    [InlineData("x/1", 25.0)]
    [InlineData("30/x", 25.0)]
    [InlineData("30/0", 25.0)]
    [InlineData("30/-1", 25.0)]
    [InlineData("0/0", 25.0)]
    public void Fps_parsing(string rate, double fps) => Assert.Equal(fps, ProbeService.ParseFps(rate));

    [Fact]
    public async Task Nonzero_exit_logs_redacted_stderr_and_returns_null()
    {
        _runner.Result = () => new ProcessResult(1, "", $"[hls] Opening '{Url}' failed\n\nServer returned 403\n");

        Assert.Null(await _probe.ProbeStreamAsync(Url, Ct));

        Assert.True(_log.Has(LogLevel.Warning, "ffprobe failed (exit 1)"));
        Assert.Equal(2, _log.Entries.Count(e => e.Message.StartsWith("ffprobe stderr:", StringComparison.Ordinal)));
        Assert.True(_log.Has(LogLevel.Warning, "Server returned 403"));
        Assert.DoesNotContain("SECRET", _log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_is_logged_and_returns_null()
    {
        _runner.Result = () => throw new TimeoutException();
        Assert.Null(await _probe.ProbeStreamAsync(Url, Ct));
        Assert.True(_log.Has(LogLevel.Warning, "ffprobe timed out for"));
        Assert.DoesNotContain("SECRET", _log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_errors_are_logged_and_return_null()
    {
        _runner.Result = () => throw new Win32Exception($"No such file: ffprobe {Url}");
        Assert.Null(await _probe.ProbeStreamAsync(Url, Ct));
        Assert.True(_log.Has(LogLevel.Warning, "ffprobe error for"));
        Assert.DoesNotContain("SECRET", _log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cts.CancelAsync();
        _runner.Result = () => throw new OperationCanceledException(cts.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => _probe.ProbeStreamAsync(Url, cts.Token));
    }

    [Fact]
    public void Default_constructor_uses_system_runner() => Assert.NotNull(new ProbeService());

    // ---- SystemProcessRunner (real processes; Linux only) ----

    [Fact]
    public async Task System_runner_captures_output_and_exit_code()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "needs /bin/sh");
        var result = await SystemProcessRunner.Instance.RunAsync(
            "/bin/sh", ["-c", "printf out; printf err >&2; exit 3"], TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(new ProcessResult(3, "out", "err"), result);
    }

    [Fact]
    public async Task System_runner_kills_on_timeout()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "needs /bin/sh");
        await Assert.ThrowsAsync<TimeoutException>(() =>
            SystemProcessRunner.Instance.RunAsync("/bin/sh", ["-c", "sleep 30"], TimeSpan.FromMilliseconds(200), Ct));
    }

    [Fact]
    public async Task System_runner_honours_cancellation()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "needs /bin/sh");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SystemProcessRunner.Instance.RunAsync("/bin/sh", ["-c", "sleep 30"], TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task System_runner_missing_executable_throws()
    {
        await Assert.ThrowsAsync<Win32Exception>(() =>
            SystemProcessRunner.Instance.RunAsync("/nonexistent/ffprobe", [], TimeSpan.FromSeconds(5), Ct));
    }
}

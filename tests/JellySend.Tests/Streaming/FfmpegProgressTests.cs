using JellySend.Streaming.Diagnostics;
using JellySend.Streaming.Video;

namespace JellySend.Tests.Streaming;

/// <summary>Spec 017: FFmpeg's -progress blocks become the FFMPEG stats.</summary>
public sealed class FfmpegProgressTests
{
    [Fact]
    public void A_block_is_published_at_its_progress_line_and_never_logged()
    {
        var diagnostics = new StreamDiagnostics(24, "1280:720", "5000k");
        var progress = new FfmpegProgress(diagnostics);
        string[] block = ["frame=480", "fps=23.98", "stream_0_0_q=-0.0", "bitrate=N/A", "total_size=N/A", "out_time_us=20000000",
            "out_time_ms=20000000", "out_time=00:00:20.000000", "dup_frames=2", "drop_frames=1", "speed=1.01x"];
        Assert.All(block, line => Assert.True(progress.Feed(line)));
        Assert.Null(diagnostics.Snapshot().EncodeSpeed); // not published before the block ends
        Assert.True(progress.Feed("progress=continue"));
        var s = diagnostics.Snapshot();
        Assert.Equal((23.98, 1.01, 2L, 1L), (s.EncodeFps!.Value, s.EncodeSpeed!.Value, s.EncodeDupFrames!.Value, s.EncodeDropFrames!.Value));
    }

    [Theory]
    [InlineData("[h264 @ 0x55] error while decoding MB 3 4")]
    [InlineData("Stream map '0:3' matches no streams.")]
    [InlineData("=value")]
    [InlineData("color=red is not a progress key")]
    public void Other_lines_are_warnings(string line) =>
        Assert.False(new FfmpegProgress(new StreamDiagnostics(24, "1:1", "1k")).Feed(line));

    [Fact]
    public void Not_available_values_are_unknown_and_reset_forgets_the_last_block()
    {
        var diagnostics = new StreamDiagnostics(24, "1280:720", "5000k");
        var progress = new FfmpegProgress(diagnostics);
        progress.Feed("speed=N/A");
        progress.Feed("fps=0.00");
        progress.Feed("progress=continue");
        Assert.Null(diagnostics.Snapshot().EncodeSpeed);
        Assert.Equal(0.0, diagnostics.Snapshot().EncodeFps);

        progress.Feed("speed=0.5x");
        progress.Reset();
        progress.Feed("progress=end");
        Assert.Null(diagnostics.Snapshot().EncodeSpeed);
    }
}

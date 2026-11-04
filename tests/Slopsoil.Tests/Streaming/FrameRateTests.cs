using Slopsoil.Streaming.Video;

namespace Slopsoil.Tests.Streaming;

/// <summary>Spec 007: STREAM_FPS is a ceiling; the output follows the source frame rate.</summary>
public sealed class FrameRateTests
{
    [Theory]
    [InlineData(23.976025, 24000.0 / 1001.0)]
    [InlineData(23.976, 24000.0 / 1001.0)]
    [InlineData(29.97003, 30000.0 / 1001.0)]
    [InlineData(59.94006, 60000.0 / 1001.0)]
    [InlineData(24.0, 24.0)]
    [InlineData(25.000001, 25.0)]
    [InlineData(12.5, 12.5)]
    public void Normalize_snaps_ntsc_and_whole_rates(double reported, double expected) =>
        Assert.Equal(expected, FrameRate.Normalize(reported)!.Value, 9);

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1000.0)]
    [InlineData(90000.0)]
    [InlineData(double.NaN)]
    public void Normalize_rejects_unknown_or_implausible_rates(double? reported) =>
        Assert.Null(FrameRate.Normalize(reported));

    [Theory]
    [InlineData(23.976, 30.0, 24000.0 / 1001.0)] // film: follow the source
    [InlineData(24.0, 30.0, 24.0)]
    [InlineData(25.0, 30.0, 25.0)]
    [InlineData(30.0, 30.0, 30.0)]
    [InlineData(29.97, 30.0, 30000.0 / 1001.0)]
    [InlineData(60.0, 30.0, 30.0)]                // above the ceiling: even division
    [InlineData(59.94, 30.0, 30000.0 / 1001.0)]
    [InlineData(50.0, 30.0, 25.0)]
    [InlineData(120.0, 30.0, 30.0)]
    [InlineData(48.0, 30.0, 24.0)]
    [InlineData(60.0, 60.0, 60.0)]
    [InlineData(30.0, 24.0, 15.0)]
    public void Choose_follows_the_source_up_to_the_ceiling(double source, double ceiling, double expected) =>
        Assert.Equal(expected, FrameRate.Choose(source, ceiling), 9);

    [Theory]
    [InlineData(null)]
    [InlineData(1000.0)]
    public void Choose_falls_back_to_the_ceiling_when_the_source_is_unknown(double? source) =>
        Assert.Equal(30.0, FrameRate.Choose(source, 30.0));

    [Theory]
    [InlineData(24000.0 / 1001.0, "24000/1001")]
    [InlineData(60000.0 / 1001.0, "60000/1001")]
    [InlineData(30.0, "30")]
    [InlineData(25.0, "25")]
    [InlineData(12.5, "12.5")]
    public void Ffmpeg_rate_is_an_exact_rational_for_ntsc(double fps, string expected) =>
        Assert.Equal(expected, FrameRate.ToFfmpeg(fps));

    [Theory]
    [InlineData(24000.0 / 1001.0, "23.98")]
    [InlineData(25.0, "25")]
    [InlineData(30000.0 / 1001.0, "29.97")]
    public void Format_is_short(double fps, string expected) => Assert.Equal(expected, FrameRate.Format(fps));
}

using Slopsoil.Streaming.Video;

namespace Slopsoil.Tests.Streaming;

/// <summary>Spec 010: runtime output height → 16:9 box and a bitrate scaled with the pixels.</summary>
public sealed class OutputSizeTests
{
    [Theory]
    [InlineData(360, "640:360")]
    [InlineData(480, "854:480")]
    [InlineData(540, "960:540")]
    [InlineData(720, "1280:720")]
    [InlineData(1080, "1920:1080")]
    public void Box_is_16_by_9_with_an_even_width(int height, string box) => Assert.Equal(box, OutputSize.Box(height));

    [Theory]
    [InlineData(720, "1280:720", "4000k", "1280:720", "4000k")]   // same as configured
    [InlineData(480, "1280:720", "4000k", "854:480", "1779k")]    // 409 920 / 921 600 pixels
    [InlineData(360, "1280:720", "4000k", "640:360", "1000k")]
    [InlineData(1080, "1280:720", "4000k", "1920:1080", "4000k")] // larger: never more bitrate than configured
    [InlineData(360, "1920:1080", "1M", "640:360", "300k")]       // floor
    [InlineData(540, "1920x1080", "6000k", "960:540", "1500k")]
    [InlineData(480, "1280:720", "fast", "854:480", "fast")]      // unparsable bitrate: kept
    [InlineData(480, "wide", "4000k", "854:480", "4000k")]        // unparsable resolution: bitrate kept
    public void For_scales_the_bitrate_down_only(int height, string resolution, string bitrate, string box, string scaled) =>
        Assert.Equal((box, scaled), OutputSize.For(height, resolution, bitrate));

    [Theory]
    [InlineData("4000k", 4000)]
    [InlineData("4000K", 4000)]
    [InlineData("4M", 4000)]
    [InlineData("4.5m", 4500)]
    [InlineData("4000000", 4000)]
    [InlineData(" 2500k ", 2500)]
    [InlineData("", null)]
    [InlineData("k", null)]
    [InlineData("-5k", null)]
    [InlineData("0k", null)]
    public void Parse_kbps(string text, int? kbps) => Assert.Equal(kbps, OutputSize.ParseKbps(text));

    [Theory]
    [InlineData(EncoderConfig.Nvenc)]
    [InlineData(EncoderConfig.Vaapi)]
    [InlineData(EncoderConfig.Libx264)]
    [InlineData(EncoderConfig.Libopenh264)]
    public void Encoders_rebuild_for_another_box_and_bitrate(string name)
    {
        var configs = new[]
        {
            EncoderConfig.ForNvenc("1280:720", "4000k"), EncoderConfig.ForVaapi("1280:720", "4000k"),
            EncoderConfig.ForLibx264("1280:720", "4000k"), EncoderConfig.ForLibopenh264("1280:720", "4000k"),
        };
        var rebuilt = configs.Single(c => c.Name == name).For("854:480", "1779k");
        Assert.Equal(name, rebuilt.Name);
        Assert.Contains("854", rebuilt.Vf, StringComparison.Ordinal);
        Assert.DoesNotContain("1280", rebuilt.Vf, StringComparison.Ordinal);
        Assert.Contains("1779k", rebuilt.PostCodec);
        Assert.DoesNotContain("4000k", rebuilt.PostCodec);
    }
}

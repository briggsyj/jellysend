using System.ComponentModel;
using Slopsoil.Streaming.Process;
using Slopsoil.Streaming.Video;

namespace Slopsoil.Tests.Streaming;

/// <summary>Spec 011: which originals the bot's FFmpeg can decode.</summary>
public sealed class DecoderSetTests
{
    // Excerpt of `ffmpeg -decoders` from the production image (Fedora ffmpeg-free).
    private const string FfmpegFree = """
        Decoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .F.... = Frame-level multithreading
         ------
         V....D libopenh264          OpenH264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D libdav1d             dav1d AV1 decoder by VideoLAN (codec av1)
         VFS..D vp9                  Google VP9
         A....D ac3                  ATSC A/52A (AC-3)
         A....D dca                  DCA (DTS Coherent Acoustics) (codec dts)
         A....D truehd               TrueHD
         A....D aac                  AAC (Advanced Audio Coding)
         S..... subrip               SubRip subtitle
        """;

    private const string FullFfmpeg = """
         VFS..D h264                 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10
         V....D libopenh264          OpenH264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         VFS..D hevc                 HEVC (High Efficiency Video Coding)
         A....D eac3                 ATSC A/52B (AC-3, E-AC-3)
        """;

    [Fact]
    public void Parses_decoders_by_codec_id()
    {
        var set = DecoderSet.Parse(FfmpegFree);
        Assert.Equal(["aac", "ac3", "av1", "dts", "h264", "subrip", "truehd", "vp9"], set.Codecs.Order());
    }

    [Theory]
    [InlineData("hevc", null, false, false)]   // ffmpeg-free: no HEVC decoder
    [InlineData("h264", null, false, true)]
    [InlineData("h264", 8, false, true)]
    [InlineData("h264", 10, false, false)]     // OpenH264: no High 10
    [InlineData("H264", null, true, false)]    // OpenH264: no interlaced
    [InlineData("av1", 10, false, true)]
    [InlineData(null, null, false, true)]      // unknown: let FFmpeg try
    public void Video_on_ffmpeg_free(string? codec, int? bitDepth, bool interlaced, bool ok) =>
        Assert.Equal(ok, DecoderSet.Parse(FfmpegFree).CanDecodeVideo(codec, bitDepth, interlaced));

    [Theory]
    [InlineData("h264", 10, true, true)]       // a native H.264 decoder handles both
    [InlineData("hevc", 10, false, true)]
    public void Video_on_full_ffmpeg(string codec, int? bitDepth, bool interlaced, bool ok) =>
        Assert.Equal(ok, DecoderSet.Parse(FullFfmpeg).CanDecodeVideo(codec, bitDepth, interlaced));

    [Theory]
    [InlineData("ac3", true)]
    [InlineData("dts", true)]
    [InlineData("truehd", true)]
    [InlineData("eac3", false)]                // ffmpeg-free: no E-AC-3 decoder
    [InlineData(null, true)]
    public void Audio_on_ffmpeg_free(string? codec, bool ok) => Assert.Equal(ok, DecoderSet.Parse(FfmpegFree).CanDecodeAudio(codec));

    [Fact]
    public void Detect_runs_ffmpeg_decoders()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(0, FfmpegFree, ""));
        var set = DecoderSet.Detect(runner, executable: "ff");
        Assert.NotNull(set);
        Assert.Equal(["ff", "-hide_banner", "-decoders"], runner.Calls.Single());
    }

    [Fact]
    public void Detect_returns_null_without_ffmpeg_or_decoders()
    {
        Assert.Null(DecoderSet.Detect(new ScriptedRunner(_ => throw new Win32Exception("missing"))));
        Assert.Null(DecoderSet.Detect(new ScriptedRunner(_ => throw new TimeoutException())));
        Assert.Null(DecoderSet.Detect(new ScriptedRunner(_ => new ProcessResult(1, "", "boom"))));
    }

    private sealed class ScriptedRunner(Func<IReadOnlyList<string>, ProcessResult> run) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public IMediaProcess Start(IReadOnlyList<string> args) => throw new NotSupportedException();

        public ProcessResult Run(IReadOnlyList<string> args, TimeSpan timeout)
        {
            Calls.Add(args);
            return run(args);
        }
    }
}

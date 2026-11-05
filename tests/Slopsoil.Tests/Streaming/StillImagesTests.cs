using System.Diagnostics;
using Slopsoil.Streaming.Process;
using Slopsoil.Streaming.Video;

namespace Slopsoil.Tests.Streaming;

/// <summary>Spec 015: encoding the loading / pause images.</summary>
public sealed class StillImagesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("slopsoil-still-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly EncoderConfig Sw = EncoderConfig.ForLibopenh264("320:240", "300k");

    /// <summary>Writes a tiny Annex-B stream to the output path (the last argument) like FFmpeg would.</summary>
    private sealed class FakeEncoder(byte[]? output, int exitCode = 0) : IProcessRunner
    {
        public int Runs;

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public IMediaProcess Start(IReadOnlyList<string> args) => throw new NotSupportedException();

        public ProcessResult Run(IReadOnlyList<string> args, TimeSpan timeout)
        {
            Interlocked.Increment(ref Runs);
            lock (Calls)
            {
                Calls.Add(args);
            }

            if (output is not null)
            {
                File.WriteAllBytes(args[^1], output);
            }

            return new ProcessResult(exitCode, "", exitCode == 0 ? "" : "boom");
        }
    }

    private static readonly byte[] Stream = [.. Nals.AnnexB(Nals.Aud, Nals.Sps, Nals.Pps, Nals.Idr, Nals.Aud, Nals.P1, Nals.Aud, Nals.Idr, Nals.Aud), .. Nals.StartCode];

    [Fact]
    public void Encode_runs_the_stream_encoder_on_a_looped_image()
    {
        var runner = new FakeEncoder(Stream);
        var clip = StillClip.Encode(runner, "/img/card.jpg", Sw, 24000.0 / 1001.0, _dir, executable: "ff");
        Assert.NotNull(clip);
        Assert.Equal(3, clip.Frames.Count);
        Assert.True(StillClip.HasSps(clip.Frames[2])); // the second keyframe got SPS/PPS
        var args = runner.Calls.Single();
        Assert.Equal("ff", args[0]);
        Assert.Equal(["-loop", "1", "-i", "/img/card.jpg"], args.SkipWhile(a => a != "-loop").Take(4));
        Assert.Contains("24000/1001", args);
        Assert.Equal("24", args[args.ToList().IndexOf("-g") + 1]);
        Assert.Contains(Sw.Vf + ",format=yuv420p", args); // images may be 4:4:4: software encoders need 4:2:0
        Assert.Equal(EncoderConfig.ForVaapi("320:240", "1k").Vf, StillClip.StillFilter(EncoderConfig.ForVaapi("320:240", "1k")));
        Assert.Empty(Directory.GetFiles(_dir)); // the temporary output is removed
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Encode_failures_give_no_clip(int exitCode)
    {
        Assert.Null(StillClip.Encode(new FakeEncoder(exitCode == 0 ? [.. Nals.AnnexB(Nals.Aud, Nals.P1, Nals.Aud), .. Nals.StartCode] : null, exitCode), "x", Sw, 25, _dir));
    }

    [Fact]
    public async Task Images_are_encoded_once_per_setup_and_absent_images_give_no_getter()
    {
        var runner = new FakeEncoder(Stream);
        var stills = new StillImages("/img/load.jpg", null, runner) { TempDirectory = _dir };
        Assert.Null(stills.Paused(Sw, 25));
        var a = stills.Loading(Sw, 25)!;
        var b = stills.Loading(Sw, 25)!;
        await Waits.UntilAsync(() => a() is not null, "encoded");
        Assert.Same(a(), b());
        Assert.Equal(1, runner.Runs);
        var c = stills.Loading(Sw, 30)!; // another frame rate: another clip
        await Waits.UntilAsync(() => c() is not null, "encoded at 30");
        Assert.Equal(2, runner.Runs);
    }

    [Fact]
    public void Real_ffmpeg_encodes_a_looping_card()
    {
        var encoder = SoftwareEncoder();
        Assert.SkipUnless(encoder is not null, "ffmpeg with libopenh264 or libx264 not installed");
        var image = Path.Combine(_dir, "card.png");
        using (var p = Process.Start(new ProcessStartInfo("ffmpeg", $"-hide_banner -loglevel error -y -f lavfi -i testsrc2=size=640x480 -frames:v 1 {image}") { RedirectStandardError = true })!)
        {
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }

        var clip = StillClip.Encode(SystemProcessRunner.Instance, image, encoder!, 25, _dir);
        Assert.NotNull(clip);
        Assert.InRange(clip.Frames.Count, 45, 55);  // 2 s at 25 fps
        Assert.True(StillClip.IsKeyframe(clip.Frames[0]) && StillClip.HasSps(clip.Frames[0]));
        Assert.All(clip.Frames.Where(StillClip.IsKeyframe), f => Assert.True(StillClip.HasSps(f)));
    }

    [Theory]
    [InlineData("loading.png")]
    [InlineData("paused.png")]
    public void Bundled_cards_exist_and_encode(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "assets", "cards")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var path = Path.Combine(dir.FullName, "assets", "cards", file);
        Assert.True(File.Exists(path), $"{path} is missing");
        var encoder = SoftwareEncoder();
        Assert.SkipUnless(encoder is not null, "ffmpeg with libopenh264 or libx264 not installed");
        var clip = StillClip.Encode(SystemProcessRunner.Instance, path, encoder!, 24000.0 / 1001.0, _dir);
        Assert.NotNull(clip);
        Assert.InRange(clip.Frames.Count, 46, 50);
    }

    private static EncoderConfig? SoftwareEncoder()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -encoders") { RedirectStandardOutput = true })!;
            var encoders = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode != 0 ? null
                : encoders.Contains("libopenh264", StringComparison.Ordinal) ? Sw
                : encoders.Contains("libx264", StringComparison.Ordinal) ? EncoderConfig.ForLibx264("320:240", "300k")
                : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

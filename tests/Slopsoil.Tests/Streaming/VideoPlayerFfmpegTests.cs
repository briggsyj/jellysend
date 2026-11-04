using Slopsoil.Streaming.Process;
using Slopsoil.Streaming.Video;

namespace Slopsoil.Tests.Streaming;

/// <summary>The video player end to end against a real FFmpeg with libx264 (skipped when it is missing).</summary>
public sealed class VideoPlayerFfmpegTests : IDisposable
{
    private static readonly bool HaveX264 = Probe();

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("slopsoil-vpf");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Plays_a_file_to_its_natural_end_with_audio_on_the_fifo()
    {
        Assert.SkipUnless(HaveX264, "ffmpeg with libx264 not available");
        var source = MakeSource(seconds: 2);
        var sink = new RecordingFrameSink();
        using var player = new VideoPlayer(Options(source), sink);
        player.Start();
        var audio = FifoDrainer.Start(player);

        Assert.Equal(VideoPlayerEnd.Natural, await player.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.InRange(sink.Count, 18, 22); // 2 s at exactly 10 fps
        Assert.All(sink.Frames, f => Assert.Single(f, n => (n[0] & 0x1F) is 1 or 5));
        Assert.Equal(7, sink.Frames[0][0][0] & 0x1F); // first frame starts with the (rewritten) SPS
        Assert.True(player.FirstFrameSent.IsCompletedSuccessfully);
        Assert.False(File.Exists(player.AudioFifoPath));
        await Waits.UntilAsync(() => audio.Bytes >= 300_000, "PCM on the FIFO"); // ~2 s of 48 kHz stereo s16le
    }

    [Fact]
    public async Task Overlay_toggle_restarts_in_place_and_stop_ends_it()
    {
        Assert.SkipUnless(HaveX264, "ffmpeg with libx264 not available");
        var source = MakeSource(seconds: 20);
        var sink = new RecordingFrameSink();
        using var player = new VideoPlayer(Options(source), sink);
        player.Start();
        FifoDrainer.Start(player);
        await player.FirstFrameSent.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        player.SetDebugOverlay(true);
        await Waits.UntilAsync(() => player.RestartSeq == 1 && player.SegmentFrames >= 3, "frames after the restart");
        Assert.True(File.Exists(player.OverlayPath));
        Assert.Contains("VIDEO", await File.ReadAllTextAsync(player.OverlayPath, TestContext.Current.CancellationToken));

        player.Stop();
        Assert.Equal(VideoPlayerEnd.Stopped, await player.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(player.OverlayPath));
        Assert.False(File.Exists(player.AudioFifoPath));
        Assert.True(player.Position > 0);
    }

    private VideoPlayerOptions Options(string source) => new()
    {
        Url = source,
        Encoder = SingleSliceX264(),
        Fps = 10,
        Live = false,
        VideoSsrc = (uint)Random.Shared.Next(1_000_000, 2_000_000),
        TempDirectory = _dir.FullName,
        Resolution = "320:240",
        Bitrate = "500k",
    };

    /// <summary>
    /// libx264 on one thread: the zerolatency tune otherwise uses sliced threads (several slices per picture),
    /// which <c>AccessUnitSplitter</c> splits into separate frames because it starts a frame on every slice NAL.
    /// </summary>
    private static EncoderConfig SingleSliceX264()
    {
        var x264 = EncoderConfig.ForLibx264("320:240", "500k");
        return x264 with { PostCodec = [.. x264.PostCodec, "-threads", "1"] };
    }

    private string MakeSource(int seconds)
    {
        var path = Path.Combine(_dir.FullName, "src.mp4");
        var r = SystemProcessRunner.Instance.Run(
            [
                "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=10", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast",
                "-c:a", "aac", path,
            ],
            TimeSpan.FromSeconds(60));
        Assert.True(r.ExitCode == 0, r.StandardError);
        return path;
    }

    private static bool Probe()
    {
        try
        {
            return new EncoderDetector(SystemProcessRunner.Instance).TestEncoder("libx264", []);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Stands in for the audio sender: reads the FIFO (reopening after each FFmpeg) while the player runs.</summary>
    private sealed class FifoDrainer
    {
        private long _bytes;

        public long Bytes => Interlocked.Read(ref _bytes);

        public static FifoDrainer Start(VideoPlayer player)
        {
            var drainer = new FifoDrainer();
            new Thread(() =>
            {
                var buffer = new byte[65_536];
                try
                {
                    while (player.IsSourceActive)
                    {
                        using var fifo = new FileStream(player.AudioFifoPath, FileMode.Open, FileAccess.Read);
                        int n;
                        while ((n = fifo.Read(buffer)) > 0)
                        {
                            Interlocked.Add(ref drainer._bytes, n);
                        }
                    }
                }
                catch (IOException)
                {
                    // the player removed the FIFO
                }
            })
            { IsBackground = true }.Start();
            return drainer;
        }
    }
}

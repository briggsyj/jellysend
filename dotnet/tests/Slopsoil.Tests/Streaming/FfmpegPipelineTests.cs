using System.Buffers.Binary;
using System.Diagnostics;
using Slopsoil.Common.Config;
using Slopsoil.Discord.Dave;
using Slopsoil.Streaming.Engine;

namespace Slopsoil.Tests.Streaming;

/// <summary>Real FFmpeg end to end: file → video player + audio sender → RTP datagrams on a fake go-live link.</summary>
public sealed class FfmpegPipelineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("slopsoil-pipe-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static bool HasFfmpeg()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -version") { RedirectStandardOutput = true })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private string MakeClip(double seconds)
    {
        var path = Path.Combine(_dir, "clip.mp4");
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=size=320x240:rate=30:duration={seconds}",
                     "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={seconds}", "-c:v", "libx264", "-c:a", "aac", "-shortest", path])
        {
            info.ArgumentList.Add(a);
        }

        using var p = Process.Start(info)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return path;
    }

    private static SlopsoilOptions Options() => SlopsoilOptions.From(new Dictionary<string, string?>
    {
        ["STREAM_RESOLUTION"] = "320:240",
        ["STREAM_FPS"] = "30",
        ["STREAM_VIDEO_BITRATE"] = "500k",
    });

    [Fact]
    public async Task Plays_a_file_to_its_natural_end_with_audio_and_video_packets()
    {
        Assert.SkipUnless(HasFfmpeg(), "ffmpeg not installed");
        var clip = MakeClip(3);
        var factory = new FfmpegPipelineFactory(Options()) { TempDirectory = _dir };
        Assert.True(factory.EncoderAvailable);
        var link = new CapturingLink();
        var pipeline = factory.Create(new StreamRequest { Title = "clip", Url = clip, Live = false, SourceCodec = "h264", RuntimeSeconds = 3 }, link);
        pipeline.Start();

        var end = await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(PipelineEndReason.Natural, end.Reason);
        Assert.True(pipeline.Playback.FirstFrameSent.IsCompletedSuccessfully);
        Assert.InRange(pipeline.Playback.Position, 2.5, 3.5);
        Assert.InRange(link.Audio, 100, 200);   // ~150 × 20 ms
        Assert.True(link.Video >= 90, $"video packets: {link.Video}");
        Assert.Contains("VIDEO", pipeline.Playback.DiagnosticsText(), StringComparison.Ordinal);
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task Stop_ends_a_running_stream_and_pause_seek_work_live()
    {
        Assert.SkipUnless(HasFfmpeg(), "ffmpeg not installed");
        var clip = MakeClip(20);
        var factory = new FfmpegPipelineFactory(Options()) { TempDirectory = _dir };
        var link = new CapturingLink();
        var pipeline = factory.Create(new StreamRequest { Title = "clip", Url = clip, Live = false }, link);
        pipeline.Start();
        await pipeline.Playback.FirstFrameSent.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        pipeline.Playback.Seek(10);
        for (var i = 0; i < 200 && pipeline.Playback.Position < 10.2; i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.InRange(pipeline.Playback.Position, 10.0, 12.0);
        pipeline.Playback.Pause();
        Assert.True(pipeline.Playback.IsPaused);
        pipeline.Playback.Resume();

        await pipeline.StopAsync();
        Assert.Equal(PipelineEndReason.Stopped, (await pipeline.Completion).Reason);
        Assert.False(pipeline.Playback.IsSourceActive);
    }

    [Fact]
    public async Task Missing_input_fails_to_start_without_leaking_the_fifo_reader()
    {
        Assert.SkipUnless(HasFfmpeg(), "ffmpeg not installed");
        var factory = new FfmpegPipelineFactory(Options()) { TempDirectory = _dir, FifoTimeout = TimeSpan.FromSeconds(2) };
        var pipeline = factory.Create(new StreamRequest { Title = "x", Url = Path.Combine(_dir, "missing.mp4"), Live = false }, new CapturingLink());
        pipeline.Start();
        var end = await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(new PipelineEnd(PipelineEndReason.Failed, "go-live stream failed to start"), end);
        await pipeline.StopAsync();
    }

    [Fact]
    public void No_encoder_means_no_pipeline()
    {
        var factory = new FfmpegPipelineFactory(Options(), detect: () => null);
        Assert.False(factory.EncoderAvailable);
        Assert.Throws<InvalidOperationException>(() => factory.Create(new StreamRequest { Title = "x", Url = "y" }, new CapturingLink()));
    }

    private sealed class CapturingLink : IGoLiveLink
    {
        private int _audio;
        private int _video;

        public int Audio => Volatile.Read(ref _audio);

        public int Video => Volatile.Read(ref _video);

        public string Mode => "aead_xchacha20_poly1305_rtpsize";

        public byte[] SecretKey { get; } = new byte[32];

        public uint AudioSsrc => 100;

        public uint VideoSsrc => 101;

        public IDaveSession? Dave => null;

        public bool SendPacket(ReadOnlySpan<byte> datagram)
        {
            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]);
            Interlocked.Increment(ref ssrc == 100 ? ref _audio : ref _video);
            return true;
        }

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

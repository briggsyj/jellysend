using System.Net.Sockets;
using Slopsoil.Discord.Rtp;
using Slopsoil.Streaming;
using Slopsoil.Streaming.Contracts;
using Slopsoil.Streaming.Diagnostics;
using Slopsoil.Tests.Discord;

namespace Slopsoil.Tests.Streaming;

public sealed class StreamDiagnosticsTests
{
    private readonly ManualTimeProvider _time = new();

    private StreamDiagnostics New() => new(30, "1920:1080", "12000k", _time);

    [Fact]
    public void Snapshot_has_the_data_model_fields()
    {
        string[] fields =
        [
            "FpsSent", "FpsTarget", "LateFrames", "MaxLatenessMs", "ReadStallMs", "PacketsPerSec", "SendDrops", "SendErrors",
            "DriftMs", "SilenceSteps", "SkipSteps", "Encoder", "Resolution", "Bitrate", "SourceCodec", "SourceFps", "Position", "Runtime",
            "VideoBufferSeconds", "AudioBufferSeconds",
        ];
        var props = typeof(StreamDiagnosticsSnapshot).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.All(fields, f => Assert.Contains(f, props));
        Assert.Equal(fields.Length + 3, props.Count); // + Window, Frames, LateTotalMs for the log line
    }

    [Fact]
    public void Empty_snapshot()
    {
        var s = New().Snapshot();
        Assert.Equal(0, s.FpsSent);
        Assert.Equal(30, s.FpsTarget);
        Assert.Equal(0, s.PacketsPerSec);
        Assert.Null(s.DriftMs);
        Assert.Equal("?", s.Encoder);
        Assert.Equal("1920:1080", s.Resolution);
        Assert.Equal("12000k", s.Bitrate);
        Assert.Null(s.SourceCodec);
        Assert.Null(s.Runtime);
        Assert.Equal(0, s.Position);
        Assert.Equal(TimeSpan.Zero, s.Window);
        Assert.Equal((0.0, 0.0), (s.VideoBufferSeconds, s.AudioBufferSeconds));
    }

    [Fact]
    public void Buffer_fill_is_the_latest_report()
    {
        var d = New();
        d.VideoBuffered(TimeSpan.FromSeconds(4.5));
        d.AudioBuffered(TimeSpan.FromSeconds(5.5));
        d.AudioBuffered(TimeSpan.FromSeconds(5.25));
        var s = d.Snapshot();
        Assert.Equal((4.5, 5.25), (s.VideoBufferSeconds, s.AudioBufferSeconds));
    }

    [Fact]
    public void Counts_and_rates_over_the_window()
    {
        var d = New();
        d.SetEncoder("libx264");
        d.SetSource("hevc", 5400);
        for (var i = 0; i < 50; i++)
        {
            d.VideoFrameSent(TimeSpan.FromMilliseconds(5));
            d.PacketSent();
            d.PacketSent();
            _time.Advance(TimeSpan.FromMilliseconds(40));
        }

        d.VideoFrameSent(TimeSpan.FromMilliseconds(-12));
        d.VideoFrameSent(TimeSpan.Zero); // exactly on the deadline counts as late, like Python
        d.VideoFrameSent(TimeSpan.FromMilliseconds(-30));
        d.VideoReadStall(TimeSpan.FromMilliseconds(7));
        d.VideoReadStall(TimeSpan.FromMilliseconds(3));
        d.PacketDropped();
        d.SendError();
        d.SendError();
        d.AudioSilenceStep();
        d.AudioSkipStep();
        d.AudioSkipStep();
        d.AudioDrift(15);
        d.AudioDrift(-4.5);

        var s = d.Snapshot();
        Assert.Equal(2.0, s.Window.TotalSeconds, 6);
        Assert.Equal(53, s.Frames);
        Assert.Equal(26.5, s.FpsSent, 6);
        Assert.Equal(3, s.LateFrames);
        Assert.Equal(30, s.MaxLatenessMs, 6);
        Assert.Equal(42, s.LateTotalMs, 6);
        Assert.Equal(10, s.ReadStallMs, 6);
        Assert.Equal(50, s.PacketsPerSec, 6);
        Assert.Equal(1, s.SendDrops);
        Assert.Equal(2, s.SendErrors);
        Assert.Equal(-4.5, s.DriftMs);
        Assert.Equal(1, s.SilenceSteps);
        Assert.Equal(2, s.SkipSteps);
        Assert.Equal("libx264", s.Encoder);
        Assert.Equal("hevc", s.SourceCodec);
        Assert.Equal(5400, s.Runtime);
    }

    [Fact]
    public void Window_rolls_over_after_ten_seconds()
    {
        var d = New();
        d.VideoFrameSent(TimeSpan.FromMilliseconds(-100)); // second 0
        d.PacketDropped();
        _time.Advance(TimeSpan.FromSeconds(5));
        d.VideoFrameSent(TimeSpan.FromMilliseconds(1)); // second 5
        _time.Advance(TimeSpan.FromSeconds(4.5));

        var full = d.Snapshot(); // t = 9.5 s: both buckets
        Assert.Equal(2, full.Frames);
        Assert.Equal(1, full.LateFrames);
        Assert.Equal(9.5, full.Window.TotalSeconds, 6);

        _time.Advance(TimeSpan.FromSeconds(1)); // t = 10.5 s: second 0 left the window
        var rolled = d.Snapshot();
        Assert.Equal(1, rolled.Frames);
        Assert.Equal(0, rolled.LateFrames);
        Assert.Equal(0, rolled.MaxLatenessMs);
        Assert.Equal(0, rolled.SendDrops);
        Assert.Equal(9.5, rolled.Window.TotalSeconds, 6);

        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(0, d.Snapshot().Frames);
    }

    [Fact]
    public void Stale_buckets_are_reused()
    {
        var d = New();
        d.PacketSent(); // second 0
        _time.Advance(TimeSpan.FromSeconds(10)); // second 10 shares the slot
        d.PacketSent();
        var s = d.Snapshot();
        Assert.Equal(1 / s.Window.TotalSeconds, s.PacketsPerSec, 6);
    }

    [Fact]
    public void Position_comes_from_the_clock()
    {
        var d = New();
        d.Clock = new FixedClock(42.5);
        Assert.Equal(42.5, d.Snapshot().Position);
    }

    [Fact]
    public void Is_safe_to_feed_from_several_threads()
    {
        var d = new StreamDiagnostics(30, "1280:720", "1M");
        Parallel.For(0, 8, TestContext.Current.CancellationToken.ToParallelOptions(), _ =>
        {
            for (var i = 0; i < 1000; i++)
            {
                d.PacketSent();
                d.AudioSilenceStep();
            }
        });
        var s = d.Snapshot();
        Assert.Equal(8000, s.SilenceSteps);
        Assert.Equal(8000, Math.Round(s.PacketsPerSec * s.Window.TotalSeconds));
    }

    [Fact]
    public void Rtp_sender_reports_sends_drops_and_errors()
    {
        var d = New();
        var outcomes = new Queue<Func<bool>>([() => true, () => false, () => throw new SocketException(), () => throw new ObjectDisposedException("udp")]);
        var sender = new RtpMediaSender(_ => outcomes.Dequeue()(), TransportCrypto.XChaChaRtpSize, new byte[32], 1, 2, () => null) { Stats = d };
        for (var i = 0; i < 4; i++)
        {
            sender.SendOpus([1, 2, 3]);
        }

        Assert.Equal(1, sender.PacketsSent);
        Assert.Equal(1, sender.PacketsDropped);
        Assert.Equal(2, sender.SendErrors);
        var s = d.Snapshot();
        Assert.Equal(1, s.SendDrops);
        Assert.Equal(2, s.SendErrors);
    }

    private sealed class FixedClock(double position) : IMediaClock
    {
        public double Fps => 30;

        public double Position => position;

        public int RestartSeq => 0;

        public double SegmentOffset => 0;

        public int SegmentFrames => 0;

        public bool SeekPending => false;

        public PlayoutAnchor? Anchor => null;

        public bool IsSourceActive => true;

        public PauseGate Pause { get; } = new();

        public IAudioFollowerHandle RegisterAudioFollower() => throw new NotSupportedException();
    }
}

internal static class ParallelOptionsExtensions
{
    public static ParallelOptions ToParallelOptions(this CancellationToken ct) => new() { CancellationToken = ct };
}

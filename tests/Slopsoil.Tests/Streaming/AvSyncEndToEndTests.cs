using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Slopsoil.Common.Config;
using Slopsoil.Discord.Dave;
using Slopsoil.Streaming.Engine;

namespace Slopsoil.Tests.Streaming;

/// <summary>Timing-sensitive real-time tests: run alone, after the parallel ones, so a busy machine can't skew them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvSyncCollection
{
    public const string Name = "A/V sync end to end";
}

/// <summary>
/// SC-001 acceptance gate (spec 004): real FFmpeg → pipeline → datagrams, with a flash + beep sync marker at media
/// 2, 12 and 16 s in a source whose audio track starts 1 s after (or before) its video. The flash's video frame and
/// the beep's audio packet must leave within one audio frame (20 ms) of each other at the start, after a seek
/// (input <c>-ss</c> into a long-GOP B-frame source) and after a pause.
/// </summary>
[Collection(AvSyncCollection.Name)]
public sealed class AvSyncLateAudioTests : IDisposable
{
    private readonly AvSyncRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Late_starting_audio_stays_in_sync_at_start_after_seek_and_after_pause()
    {
        Assert.SkipUnless(AvSyncRig.HasFfmpeg(), "ffmpeg not installed");
        await _rig.RunAsync(audioOffset: 1.0, pause: true);
    }
}

/// <inheritdoc cref="AvSyncLateAudioTests"/>
[Collection(AvSyncCollection.Name)]
public sealed class AvSyncEarlyAudioTests : IDisposable
{
    private readonly AvSyncRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Early_starting_audio_stays_in_sync_at_start_and_after_seek()
    {
        Assert.SkipUnless(AvSyncRig.HasFfmpeg(), "ffmpeg not installed");
        await _rig.RunAsync(audioOffset: -1.0, pause: false);
    }
}

/// <summary>Builds the marker clip, runs the pipeline and finds the markers on the wire.</summary>
internal sealed class AvSyncRig : IDisposable
{
    /// <summary>A flash frame (testsrc2 over black) encodes to many kB; black frames to a few dozen bytes.</summary>
    private const int FlashBytes = 5000;

    /// <summary>SC-001: within one audio frame.</summary>
    private const double ToleranceMs = 20;

    private static readonly double[] Markers = [2, 12, 16];

    private readonly string _dir = Directory.CreateTempSubdirectory("slopsoil-avsync-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static bool HasFfmpeg()
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

    public async Task RunAsync(double audioOffset, bool pause)
    {
        var ct = TestContext.Current.CancellationToken;
        var clip = MakeClip(audioOffset);
        var options = SlopsoilOptions.From(new Dictionary<string, string?>
        {
            ["STREAM_RESOLUTION"] = "320:240",
            ["STREAM_FPS"] = "30",
            ["STREAM_VIDEO_BITRATE"] = "500k",
            ["STREAM_BUFFER_SECONDS"] = "1",
        });
        var link = new TimingLink();
        var pipeline = new FfmpegPipelineFactory(options) { TempDirectory = _dir }
            .Create(new StreamRequest { Title = "sync", Url = clip, Live = false }, link);
        var offsets = new List<Marker>();
        pipeline.Start();
        try
        {
            // At the start (media 2 s).
            var first = await link.NextMarkerAsync(0, ct);
            offsets.Add(first);

            // After a seek into the middle of the file (media 12 s, 2 s into the new segment).
            pipeline.Playback.Seek(10.0);
            var mark = link.Mark();
            var second = await link.NextMarkerAsync(mark, ct);
            offsets.Add(second);

            if (pause)
            {
                // Across a pause (media 16 s).
                pipeline.Playback.Pause();
                await Task.Delay(1000, ct);
                pipeline.Playback.Resume();
                var third = await link.NextMarkerAsync(second.End, ct);
                offsets.Add(third);
            }
        }
        finally
        {
            await pipeline.StopAsync();
        }

        var report = string.Join(", ", offsets);
        TestContext.Current.SendDiagnosticMessage($"audio-minus-video at the markers (audio offset {audioOffset} s): {report}");
        if (Environment.GetEnvironmentVariable("SLOPSOIL_AVSYNC_REPORT") is { Length: > 0 } file)
        {
            File.AppendAllText(file, $"audio offset {audioOffset:+0.0;-0.0} s: {report}{Environment.NewLine}");
        }
        Assert.All(offsets, o => Assert.True(Math.Abs(o.OffsetMs) <= ToleranceMs, $"beep vs flash: {report}"));
    }

    /// <summary>
    /// 20 s, 320x240 at 30 fps, long GOP with B-frames; black except a testsrc2 frame at media 2/12/16 s; audio
    /// silent except a 100 ms 1 kHz beep at the same media times. The audio track starts
    /// <paramref name="audioOffset"/> s after the video (negative: before).
    /// </summary>
    private string MakeClip(double audioOffset)
    {
        var inv = CultureInfo.InvariantCulture;
        var videoStart = Math.Max(0, -audioOffset);
        var audioStart = Math.Max(0, audioOffset);
        var flashes = string.Join('+', Markers.Select(t => string.Create(inv, $"eq(n,{(int)Math.Round((t - videoStart) * 30)})")));
        var beeps = string.Join('+', Markers.Select(t => string.Create(inv, $"between(t,{t - audioStart},{t - audioStart + 0.1})")));
        var path = Path.Combine(_dir, "sync.mkv");
        Ffmpeg(
            "-itsoffset", videoStart.ToString(inv), "-f", "lavfi",
            "-i", $"color=black:s=320x240:r=30:d=19[bg];testsrc2=s=320x240:r=30:d=19[fg];[bg][fg]overlay=enable='{flashes}'",
            "-itsoffset", audioStart.ToString(inv), "-f", "lavfi",
            "-i", $"aevalsrc='if({beeps},0.5*sin(2*PI*1000*t),0)':s=48000:d=19:c=stereo",
            "-c:v", "libx264", "-preset", "veryfast", "-bf", "3", "-g", "250", "-c:a", "pcm_s16le", path);
        return path;
    }

    private static void Ffmpeg(params string[] args)
    {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y", .. args])
        {
            info.ArgumentList.Add(a);
        }

        using var p = Process.Start(info)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }

    internal readonly record struct Marker(double OffsetMs, int End, int Frame, int Packet)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{OffsetMs:+0.0;-0.0} ms (frame {Frame}, packet {Packet})");
    }

    /// <summary>Records every datagram's send time, stream, RTP timestamp and size (headers are in the clear).</summary>
    private sealed class TimingLink : IGoLiveLink
    {
        private readonly List<(long Ticks, bool Audio, uint Timestamp, int Size)> _packets = [];

        public string Mode => "aead_xchacha20_poly1305_rtpsize";

        public byte[] SecretKey { get; } = new byte[32];

        public uint AudioSsrc => 100;

        public uint VideoSsrc => 101;

        public IDaveSession? Dave => null;

        public int Mark()
        {
            lock (_packets)
            {
                return _packets.Count;
            }
        }

        public bool SendPacket(ReadOnlySpan<byte> datagram)
        {
            var now = Stopwatch.GetTimestamp();
            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]);
            var ts = BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]);
            lock (_packets)
            {
                _packets.Add((now, ssrc == AudioSsrc, ts, datagram.Length));
            }

            return true;
        }

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>The next flash and beep after packet <paramref name="from"/>: audio-minus-video send time.</summary>
        public async Task<Marker> NextMarkerAsync(int from, CancellationToken ct)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (Find(from) is { } found)
                {
                    return found;
                }

                await Task.Delay(20, ct);
            }

            Assert.Fail($"no flash + beep marker after packet {from}");
            return default;
        }

        private Marker? Find(int from)
        {
            (long Ticks, bool Audio, uint Timestamp, int Size)[] packets;
            lock (_packets)
            {
                packets = [.. _packets.Skip(from)];
            }

            long? flash = null;
            var flashEnd = 0;
            long frameStart = 0;
            uint? frameTs = null;
            var frameBytes = 0;
            var quietAudio = int.MaxValue;
            long? beep = null;
            var beepEnd = 0;
            var frames = -1;
            var flashFrame = 0;
            var audioPackets = 0;
            var beepPacket = 0;
            for (var i = 0; i < packets.Length; i++)
            {
                var p = packets[i];
                if (p.Audio)
                {
                    // Silence encodes to a few bytes of Opus, a 1 kHz tone to many more.
                    quietAudio = Math.Min(quietAudio, p.Size);
                    if (beep is null && p.Size > quietAudio + 40)
                    {
                        (beep, beepEnd, beepPacket) = (p.Ticks, i, audioPackets);
                    }

                    audioPackets++;

                    continue;
                }

                if (p.Timestamp != frameTs)
                {
                    if (flash is null && frameBytes > FlashBytes)
                    {
                        (flash, flashEnd, flashFrame) = (frameStart, i, frames);
                    }

                    (frameTs, frameStart, frameBytes) = (p.Timestamp, p.Ticks, 0);
                    frames++;
                }

                frameBytes += p.Size;
            }

            if (flash is not { } f || beep is not { } b)
            {
                return null;
            }

            var offsetMs = (b - f) * 1000.0 / Stopwatch.Frequency;

            return new Marker(offsetMs, from + Math.Max(flashEnd, beepEnd) + 1, flashFrame, beepPacket);
        }
    }
}

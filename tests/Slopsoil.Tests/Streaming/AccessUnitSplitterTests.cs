using Slopsoil.Discord.Rtp;
using Slopsoil.Streaming;

namespace Slopsoil.Tests.Streaming;

public sealed class AccessUnitSplitterTests
{
    private static readonly byte[] Aud = [0x09, 0xF0];
    private static readonly byte[] Pps = [0x68, 0xCE, 0x3C, 0x80];
    private static readonly byte[] Sei = [0x06, 0x05, 0x01, 0x80];
    private static readonly byte[] Idr = [0x65, 0x88, 0x84, 0x21];
    private static readonly byte[] Slice = [0x41, 0x9A, 0x02, 0x03];
    private static readonly byte[] Filler = [0x0C, 0xFF];

    private static byte[] Stream(params byte[][] nals) =>
        [.. nals.SelectMany((n, i) => (byte[])[.. (i % 2 == 0 ? new byte[] { 0, 0, 0, 1 } : [0, 0, 1]), .. n])];

    [Fact]
    public void Aud_and_slices_delimit_frames_and_parameter_sets_join_the_next_frame()
    {
        var splitter = new AccessUnitSplitter();
        var frames = splitter.Feed(Stream(Aud, Pps, Sei, Idr, Filler, Aud, Slice, Slice, Aud));

        // The final AUD has no start code after it yet, so the last frame waits for Flush.
        Assert.Equal(2, frames.Count);
        Assert.Equal([Pps, Sei, Idr], frames[0]);
        Assert.Equal([Slice], frames[1]);
        Assert.Equal([Slice], Assert.Single(splitter.Flush()));
    }

    [Fact]
    public void Continuation_slices_join_their_picture()
    {
        byte[] idrPart2 = [0x65, 0x22, 0x01];   // first_mb_in_slice != 0
        byte[] pPart2 = [0x41, 0x11, 0x02];
        var splitter = new AccessUnitSplitter();
        var frames = splitter.Feed(Stream(Pps, Idr, idrPart2, Slice, pPart2, Slice)).ToList();
        frames.AddRange(splitter.Flush());
        Assert.Equal(3, frames.Count);
        Assert.Equal([Pps, Idr, idrPart2], frames[0]);
        Assert.Equal([Slice, pPart2], frames[1]);
        Assert.Equal([Slice], frames[2]);
    }

    [Fact]
    public void Nal_split_across_chunks_is_reassembled_and_flushed_at_end()
    {
        var data = Stream(Idr, Slice, [.. Slice, 0x00, 0x00]);
        var splitter = new AccessUnitSplitter();
        var frames = new List<IReadOnlyList<byte[]>>();
        foreach (var chunk in data.Chunk(3))
        {
            frames.AddRange(splitter.Feed(chunk));
        }

        frames.AddRange(splitter.Flush());
        Assert.Equal(3, frames.Count);
        Assert.Equal([Idr], frames[0]);
        Assert.Equal([Slice], frames[1]);
        Assert.Equal([Slice], frames[2]); // trailing zero bytes trimmed
    }

    [Fact]
    public void Sps_is_rewritten()
    {
        var sps = Convert.FromHexString(Vectors.Section("sps").First().GetProperty("in").GetString()!);
        var splitter = new AccessUnitSplitter();
        _ = splitter.Feed(Stream(sps, Idr));
        var frame = Assert.Single(splitter.Flush());
        Assert.Equal(SpsRewriter.Rewrite(sps), frame[0]);
        Assert.Equal(Idr, frame[1]);
    }

    [Fact]
    public void Flush_without_start_code_or_data_is_empty()
    {
        var splitter = new AccessUnitSplitter();
        Assert.Empty(splitter.Feed([0x00, 0x00]));
        Assert.Empty(splitter.Flush());
    }
}

public sealed class AccessUnitSplitterFfmpegTests
{
    [Theory]
    [InlineData("aud=1")]
    [InlineData("aud=0")]
    [InlineData("aud=1:sliced-threads=1:threads=4")]
    public void Real_encoder_output_yields_one_access_unit_per_frame(string x264Params)
    {
        var output = RunFfmpeg(
            "-f", "lavfi", "-i", "testsrc2=size=640x480:rate=30", "-frames:v", "45", "-g", "15", "-bf", "0",
            "-c:v", "libx264", "-tune", "zerolatency", "-x264-params", x264Params, "-f", "h264", "pipe:1");
        Assert.SkipWhen(output is null, "ffmpeg with libx264 not available");

        var splitter = new AccessUnitSplitter();
        var frames = output!.Chunk(4096).SelectMany(c => splitter.Feed(c)).ToList();
        frames.AddRange(splitter.Flush()); // after the lazy Feed calls ran

        Assert.Equal(45, frames.Count);
        Assert.All(frames, f => Assert.Single(f, n => (n[0] & 0x1F) is 1 or 5 && (n[1] & 0x80) != 0));
        Assert.Equal(3, frames.Count(f => f.Any(n => (n[0] & 0x1F) == 5)));
        Assert.All(frames.Where(f => f.Any(n => (n[0] & 0x1F) == 5)), f => Assert.Equal(7, f[0][0] & 0x1F));
    }

    private static byte[]? RunFfmpeg(params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-nostdin", .. args])
        {
            info.ArgumentList.Add(a);
        }

        try
        {
            using var p = System.Diagnostics.Process.Start(info)!;
            using var ms = new MemoryStream();
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            _ = err.Result;
            return p.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

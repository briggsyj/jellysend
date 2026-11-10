using System.ComponentModel;
using JellySend.Discord.Rtp;
using JellySend.Streaming;
using JellySend.Streaming.Process;

namespace JellySend.Tests.Streaming;

/// <summary>The real process / FIFO seams against <c>/bin/sh</c> and the file system.</summary>
public sealed class ProcessSeamTests
{
    [Fact]
    public void Run_captures_output_and_exit_code()
    {
        var r = SystemProcessRunner.Instance.Run(["/bin/sh", "-c", "echo out; echo err >&2; exit 3"], TimeSpan.FromSeconds(10));
        Assert.Equal(3, r.ExitCode);
        Assert.Equal("out\n", r.StandardOutput);
        Assert.Equal("err\n", r.StandardError);
    }

    [Fact]
    public void Run_kills_an_overrunning_process()
    {
        Assert.Throws<TimeoutException>(() => SystemProcessRunner.Instance.Run(["/bin/sh", "-c", "sleep 30"], TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Missing_executable_throws_win32()
    {
        Assert.Throws<Win32Exception>(() => SystemProcessRunner.Instance.Run(["/nonexistent/ffmpeg"], TimeSpan.FromSeconds(1)));
        Assert.Throws<Win32Exception>(() => SystemProcessRunner.Instance.Start(["/nonexistent/ffmpeg"]));
    }

    [Fact]
    public void Started_process_streams_stdout_and_stderr_with_stdin_at_eof()
    {
        // cat exits at once because stdin is at EOF (like subprocess.DEVNULL).
        using var p = SystemProcessRunner.Instance.Start(["/bin/sh", "-c", "cat; printf 'abc'; echo warn >&2"]);
        using var ms = new MemoryStream();
        p.StandardOutput.CopyTo(ms);
        Assert.Equal("abc"u8.ToArray(), ms.ToArray());
        Assert.Equal("warn", p.StandardError.ReadLine());
        Assert.True(p.WaitForExit(Timeout.InfiniteTimeSpan));
        Assert.True(p.HasExited);
        Assert.Equal(0, p.ExitCode);
        p.Terminate(); // already exited: no-op
        p.Kill();
    }

    [Fact]
    public void Terminate_sends_sigterm_and_kill_sigkill()
    {
        using var termed = SystemProcessRunner.Instance.Start(["/bin/sh", "-c", "trap 'exit 7' TERM; echo ready; while :; do sleep 0.05; done"]);
        Assert.Equal((byte)'r', termed.StandardOutput.ReadByte()); // the trap is installed
        termed.Terminate();
        Assert.True(termed.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.Equal(7, termed.ExitCode);

        using var killed = SystemProcessRunner.Instance.Start(["/bin/sh", "-c", "trap '' TERM; sleep 30"]);
        killed.CloseStandardOutput();
        killed.Kill();
        Assert.True(killed.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.Throws<ObjectDisposedException>(() => killed.StandardOutput.ReadByte());
    }

    [Fact]
    public void Fifo_is_created_replaced_and_removed()
    {
        var dir = Directory.CreateTempSubdirectory("jellysend-fifo");
        try
        {
            var path = Path.Combine(dir.FullName, "a.fifo");
            File.WriteAllText(path, "stale file");
            UnixFifo.Instance.Create(path);
            Assert.Equal("600", SystemProcessRunner.Instance.Run(["/usr/bin/stat", "-c", "%a", path], TimeSpan.FromSeconds(10)).StandardOutput.Trim());
            Assert.Equal(0, SystemProcessRunner.Instance.Run(["/bin/sh", "-c", "test -p \"$0\"", path], TimeSpan.FromSeconds(10)).ExitCode);
            UnixFifo.Instance.Create(path); // re-creating over an existing FIFO works
            UnixFifo.Instance.Remove(path);
            Assert.False(File.Exists(path));
            UnixFifo.Instance.Remove(path); // missing is fine
            Assert.Throws<Win32Exception>(() => UnixFifo.Instance.Create(Path.Combine(dir.FullName, "missing", "b.fifo")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Rtp_sender_calls_the_pacer_after_each_packet_and_stops_when_told()
    {
        var sent = new List<byte[]>();
        var sender = new RtpMediaSender(d =>
        {
            sent.Add(d.ToArray());
            return true;
        }, TransportCrypto.XChaChaRtpSize, new byte[32], 1, 2, () => null);
        var big = new byte[3000];
        big[0] = 0x65;
        var calls = new List<(int, int)>();

        sender.SendVideoFrame([big], 3000, (i, n) =>
        {
            calls.Add((i, n));
            return true;
        });
        Assert.Equal([(0, 3), (1, 3), (2, 3)], calls);
        Assert.Equal(3, sent.Count);

        sender.SendVideoFrame([big], 3000, (i, _) => i < 1);
        Assert.Equal(5, sent.Count); // two packets, then abandoned
        sender.SendVideoFrame([big], 3000);
        Assert.Equal(8, sent.Count);
        var ts = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(sent[^1].AsSpan(4));
        Assert.Equal(6000u, ts); // the abandoned frame still advanced the timestamp
    }
}

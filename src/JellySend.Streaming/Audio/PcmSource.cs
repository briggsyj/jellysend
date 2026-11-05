using System.Runtime.InteropServices;

namespace JellySend.Streaming.Audio;

/// <summary>Where the audio sender reads FFmpeg's s16le 48 kHz stereo PCM from (the audio FIFO).</summary>
public interface IPcmSource
{
    /// <summary>
    /// Fill <paramref name="frame"/>, blocking until it is full or the writer closes. Returns the bytes read;
    /// fewer than <c>frame.Length</c> means the current writer closed its end (a new writer may follow).
    /// </summary>
    int ReadFrame(Span<byte> frame);

    /// <summary>Discard whatever is buffered right now without blocking; returns the bytes discarded.</summary>
    int DrainAvailable();
}

/// <summary>
/// <see cref="IPcmSource"/> over a stream, normally the read end of a POSIX FIFO. Draining asks the kernel
/// how much is buffered (<c>FIONREAD</c>) and reads only that, so it never blocks; streams without a file
/// descriptor are never drained (Python's <c>_drain_stale</c> ignores non-fd sources too).
/// </summary>
public sealed class StreamPcmSource : IPcmSource, IDisposable
{
    private const int DrainChunk = 65536;

    private readonly Stream _stream;
    private readonly SafeHandle? _fd;
    private readonly bool _ownsStream;
    private byte[]? _scratch;

    /// <param name="stream">Must be unbuffered when <paramref name="fd"/> is given, or draining misses buffered bytes.</param>
    /// <param name="fd">The stream's file descriptor, used to drain without blocking.</param>
    /// <param name="ownsStream">Dispose <paramref name="stream"/> with this source.</param>
    public StreamPcmSource(Stream stream, SafeHandle? fd = null, bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _fd = fd;
        _ownsStream = ownsStream;
    }

    /// <summary>Open a FIFO for reading (blocks until a writer opens it), unbuffered so it can be drained.</summary>
    public static StreamPcmSource OpenFifo(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
        return new StreamPcmSource(fs, fs.SafeFileHandle);
    }

    public int ReadFrame(Span<byte> frame)
    {
        var filled = 0;
        while (filled < frame.Length)
        {
            var n = _stream.Read(frame[filled..]);
            if (n == 0)
            {
                break;
            }

            filled += n;
        }

        return filled;
    }

    public int DrainAvailable()
    {
        if (_fd is null || _fd.IsInvalid || _fd.IsClosed)
        {
            return 0;
        }

        _scratch ??= new byte[DrainChunk];
        var dropped = 0;
        while (true)
        {
            var available = PosixIo.BytesAvailable(_fd);
            if (available <= 0)
            {
                return dropped;
            }

            var n = _stream.Read(_scratch, 0, Math.Min(available, DrainChunk));
            if (n == 0)
            {
                return dropped;
            }

            dropped += n;
        }
    }

    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}

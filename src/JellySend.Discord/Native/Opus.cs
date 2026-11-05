using System.Runtime.InteropServices;

namespace JellySend.Discord.Native;

/// <summary>libopus encoder for Discord voice: 48 kHz stereo, 20 ms (960-sample) frames.</summary>
public sealed partial class OpusEncoder : IDisposable
{
    private const string Lib = "opus";
    private const int OpusApplicationAudio = 2049;

    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int SamplesPerFrame = 960;
    public const int FrameBytes = SamplesPerFrame * Channels * sizeof(short); // 3840

    private IntPtr _handle;

    [LibraryImport(Lib)]
    private static partial IntPtr opus_encoder_create(int fs, int channels, int application, out int error);

    [LibraryImport(Lib)]
    private static partial void opus_encoder_destroy(IntPtr st);

    [LibraryImport(Lib)]
    private static unsafe partial int opus_encode(IntPtr st, short* pcm, int frameSize, byte* data, int maxBytes);

    public OpusEncoder()
    {
        _handle = opus_encoder_create(SampleRate, Channels, OpusApplicationAudio, out var error);
        if (error != 0 || _handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"opus_encoder_create failed ({error})");
        }
    }

    /// <summary>Encode one 20 ms frame of s16le interleaved stereo PCM (3840 bytes).</summary>
    public unsafe byte[] Encode(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length != FrameBytes)
        {
            throw new ArgumentException($"expected {FrameBytes} bytes of PCM", nameof(pcm));
        }

        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        Span<byte> output = stackalloc byte[4000];
        int written;
        fixed (byte* p = pcm)
        fixed (byte* o = output)
        {
            written = opus_encode(_handle, (short*)p, SamplesPerFrame, o, output.Length);
        }

        if (written < 0)
        {
            throw new InvalidOperationException($"opus_encode failed ({written})");
        }

        return output[..written].ToArray();
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            opus_encoder_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

using JellySend.Discord.Native;

namespace JellySend.Streaming.Audio;

/// <summary>Encodes one 20 ms frame of 48 kHz stereo s16le PCM (3840 bytes) to Opus.</summary>
public interface IAudioFrameEncoder : IDisposable
{
    byte[] Encode(ReadOnlySpan<byte> pcm);
}

/// <summary>The libopus encoder (<see cref="OpusEncoder"/>) behind <see cref="IAudioFrameEncoder"/>.</summary>
public sealed class OpusFrameEncoder : IAudioFrameEncoder
{
    private readonly OpusEncoder _encoder = new();

    public byte[] Encode(ReadOnlySpan<byte> pcm) => _encoder.Encode(pcm);

    public void Dispose() => _encoder.Dispose();
}

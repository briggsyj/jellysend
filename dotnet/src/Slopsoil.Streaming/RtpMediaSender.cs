using Slopsoil.Discord.Dave;
using Slopsoil.Discord.Rtp;

namespace Slopsoil.Streaming;

/// <summary>Sends one already-built UDP datagram; false if it was dropped.</summary>
public delegate bool PacketSink(ReadOnlySpan<byte> datagram);

/// <summary>
/// Turns Opus frames and H.264 access units into encrypted RTP datagrams for one go-live
/// connection: DAVE frame encryption (when a session is present), FU-A packetization,
/// RTP header, transport encryption. Port of <c>video_player._send_frame</c> and discord.py's
/// audio packet path. Not thread-safe per media kind; audio and video may run on separate threads.
/// </summary>
public sealed class RtpMediaSender(
    PacketSink sink, string mode, byte[] key, uint audioSsrc, uint videoSsrc, Func<IDaveSession?> dave)
{
    /// <summary>90 kHz RTP clock for video.</summary>
    public const int VideoClock = 90_000;

    /// <summary>Video nonces start in the high half so they never collide with audio's.</summary>
    public const uint VideoNonceBase = 0x8000_0000;

    private readonly object _audioLock = new();
    private readonly object _videoLock = new();
    private uint _audioNonce;
    private uint _videoNonce = VideoNonceBase;
    private long _audioSeq;
    private long _audioTs;
    private long _videoSeq;
    private long _videoTs;

    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    public long PacketsDropped => Interlocked.Read(ref _packetsDropped);

    private long _packetsSent;
    private long _packetsDropped;

    /// <summary>Send one 20 ms Opus frame (timestamp advances 960 samples).</summary>
    public void SendOpus(ReadOnlySpan<byte> opus)
    {
        var payload = dave() is { } session ? session.EncryptOpus(audioSsrc, opus) : opus.ToArray();
        lock (_audioLock)
        {
            var header = RtpHeader.Build(_audioSeq, _audioTs, audioSsrc, marker: false, RtpHeader.OpusPayloadType);
            Send(TransportCrypto.Encrypt(header, payload, mode, key, ref _audioNonce));
            _audioSeq = (_audioSeq + 1) & 0xFFFF;
            _audioTs = (_audioTs + 960) & 0xFFFF_FFFF;
        }
    }

    /// <summary>Send one access unit; all packets share a timestamp, the last carries the marker.</summary>
    public void SendVideoFrame(IReadOnlyList<byte[]> nals, int timestampIncrement)
    {
        if (nals.Count == 0)
        {
            return;
        }

        var payloads = new List<byte[]>();
        foreach (var nal in EncryptAccessUnit(nals))
        {
            payloads.AddRange(H264Packetizer.Packetize(nal));
        }

        lock (_videoLock)
        {
            for (var i = 0; i < payloads.Count; i++)
            {
                var header = RtpHeader.Build(_videoSeq, _videoTs, videoSsrc, marker: i == payloads.Count - 1);
                Send(TransportCrypto.Encrypt(header, payloads[i], mode, key, ref _videoNonce));
                _videoSeq = (_videoSeq + 1) & 0xFFFF;
            }

            _videoTs = (_videoTs + timestampIncrement) & 0xFFFF_FFFF;
        }
    }

    /// <summary>
    /// DAVE encrypts the whole Annex-B frame once; split the output back by the input NAL sizes and
    /// keep the trailer on the last NAL. Passthrough (same length) sends the NALs unchanged.
    /// </summary>
    internal IReadOnlyList<byte[]> EncryptAccessUnit(IReadOnlyList<byte[]> nals)
    {
        if (dave() is not { } session)
        {
            return nals;
        }

        var annexB = new byte[nals.Sum(n => n.Length + 4)];
        var offset = 0;
        foreach (var nal in nals)
        {
            annexB[offset + 3] = 1;
            nal.CopyTo(annexB, offset + 4);
            offset += nal.Length + 4;
        }

        var encrypted = session.EncryptH264(videoSsrc, annexB);
        if (encrypted.Length <= annexB.Length)
        {
            return nals;
        }

        var result = new List<byte[]>(nals.Count);
        offset = 0;
        foreach (var nal in nals)
        {
            offset += 4;
            result.Add(encrypted.AsSpan(offset, nal.Length).ToArray());
            offset += nal.Length;
        }

        result[^1] = [.. result[^1], .. encrypted.AsSpan(offset)];
        return result;
    }

    private void Send(byte[] datagram)
    {
        if (sink(datagram))
        {
            Interlocked.Increment(ref _packetsSent);
        }
        else
        {
            Interlocked.Increment(ref _packetsDropped);
        }
    }
}

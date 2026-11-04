using System.Buffers.Binary;
using Slopsoil.Discord.Dave;
using Slopsoil.Discord.Rtp;
using Slopsoil.Streaming;

namespace Slopsoil.Tests.Streaming;

public sealed class RtpMediaSenderTests
{
    private const int Overhead = RtpHeader.Size + 16 + 4; // header + AEAD tag + nonce suffix
    private static readonly byte[] Key = new byte[32];

    private readonly List<byte[]> _sent = [];
    private bool _accept = true;

    private RtpMediaSender Sender(IDaveSession? dave = null) =>
        new(d =>
        {
            _sent.Add(d.ToArray());
            return _accept;
        }, TransportCrypto.XChaChaRtpSize, Key, 100, 101, () => dave);

    private static (int Seq, uint Ts, uint Ssrc, bool Marker, int Pt) Header(byte[] p) =>
        (BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2)), BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4)),
         BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(8)), (p[1] & 0x80) != 0, p[1] & 0x7F);

    private static uint NonceSuffix(byte[] p) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(p.Length - 4));

    [Fact]
    public void Opus_frames_advance_sequence_and_timestamp_on_the_audio_ssrc()
    {
        var sender = Sender();
        sender.SendOpus([1, 2, 3]);
        sender.SendOpus([4, 5]);

        Assert.Equal((0, 0u, 100u, false, 120), Header(_sent[0]));
        Assert.Equal((1, 960u, 100u, false, 120), Header(_sent[1]));
        Assert.Equal(3 + Overhead, _sent[0].Length);
        Assert.Equal([0u, 1u], _sent.Select(NonceSuffix));
        Assert.Equal(2, sender.PacketsSent);
    }

    [Fact]
    public void Video_frame_is_fragmented_with_marker_on_last_packet_and_shared_timestamp()
    {
        var sender = Sender();
        var big = new byte[3000];
        big[0] = 0x65;
        sender.SendVideoFrame([[0x67, 1, 2], big], 3000);
        sender.SendVideoFrame([[0x41, 9]], 3000);
        sender.SendVideoFrame([], 3000); // ignored

        var headers = _sent.Select(Header).ToList();
        Assert.True(headers.Count >= 5);
        Assert.All(headers, h => Assert.Equal((101u, 101), (h.Ssrc, h.Pt)));
        Assert.Equal(Enumerable.Range(0, headers.Count), headers.Select(h => h.Seq));
        Assert.All(headers[..^1], h => Assert.Equal(0u, h.Ts));
        Assert.Equal([false, true], headers.Where(h => h.Ts == 0).Select(h => h.Marker).TakeLast(2));
        Assert.Equal((3000u, true), (headers[^1].Ts, headers[^1].Marker));
        Assert.Equal(RtpMediaSender.VideoNonceBase, NonceSuffix(_sent[0]));
    }

    [Fact]
    public void Dropped_packets_are_counted()
    {
        _accept = false;
        var sender = Sender();
        sender.SendOpus([1]);
        Assert.Equal((0, 1), (sender.PacketsSent, sender.PacketsDropped));
    }

    [Fact]
    public void Dave_output_is_split_by_nal_size_with_trailer_on_the_last_nal()
    {
        var dave = new TrailerDave();
        var sender = Sender(dave);
        var nals = sender.EncryptAccessUnit([[0x67, 1], [0x65, 2, 3]]);

        Assert.Equal([[0x67 ^ 0xFF, 1 ^ 0xFF], [0x65 ^ 0xFF, 2 ^ 0xFF, 3 ^ 0xFF, 0xFA, 0xFA]], nals);
        Assert.Equal(101u, dave.LastSsrc);
        sender.SendOpus([7]);
        Assert.Equal(100u, dave.LastSsrc);
        Assert.Equal(1 + 2 + Overhead, _sent[0].Length);
    }

    [Fact]
    public void Dave_passthrough_keeps_the_nals()
    {
        var sender = Sender(new PassthroughDaveSession());
        byte[][] nals = [[0x65, 1]];
        Assert.Same(nals, sender.EncryptAccessUnit(nals));
    }

    [Fact]
    public void A_disposed_dave_session_drops_instead_of_throwing()
    {
        var dave = new TrailerDave { Disposed = true };
        var sender = Sender(dave);
        sender.SendOpus([1, 2]);
        sender.SendVideoFrame([[0x65, 1]], 3000);
        Assert.Empty(_sent);
        Assert.Equal(1, sender.PacketsDropped);
    }

    /// <summary>Inverts payload bytes (the ciphertext) and appends a 2-byte trailer, like DAVE's 0xFAFA marker.</summary>
    private sealed class TrailerDave : IDaveSession
    {
        public uint LastSsrc { get; private set; }

        public int ProtocolVersion => 1;

        public bool Ready => true;

        public byte[] EncryptOpus(uint ssrc, ReadOnlySpan<byte> frame) => Encrypt(ssrc, frame);

        // Start-code bytes get inverted too; the sender drops them when splitting by NAL size.
        public byte[] EncryptH264(uint ssrc, ReadOnlySpan<byte> accessUnit) => Encrypt(ssrc, accessUnit);

        public bool Disposed { get; set; }

        private byte[] Encrypt(uint ssrc, ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            LastSsrc = ssrc;
            return [.. data.ToArray().Select(b => (byte)(b ^ 0xFF)), 0xFA, 0xFA];
        }

        public void Reinit(int protocolVersion, ulong userId, ulong channelId) => throw new NotSupportedException();

        public void Reset() => throw new NotSupportedException();

        public byte[] GetSerializedKeyPackage() => throw new NotSupportedException();

        public void SetExternalSender(ReadOnlySpan<byte> data) => throw new NotSupportedException();

        public byte[]? ProcessProposals(DaveProposalsOperation operation, ReadOnlySpan<byte> proposals, IReadOnlyCollection<ulong> recognizedUserIds) =>
            throw new NotSupportedException();

        public bool? ProcessCommit(ReadOnlySpan<byte> commit) => throw new NotSupportedException();

        public bool ProcessWelcome(ReadOnlySpan<byte> welcome, IReadOnlyCollection<ulong> recognizedUserIds) => throw new NotSupportedException();

        public void SetPassthroughMode(bool passthrough) => throw new NotSupportedException();

        public void RegisterVideoSsrc(uint ssrc) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}

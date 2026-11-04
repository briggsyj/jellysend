using System.Buffers.Binary;

namespace Slopsoil.Discord.Rtp;

/// <summary>12-byte RTP header (RFC 3550) as Discord voice/video expects it.</summary>
public static class RtpHeader
{
    public const int Size = 12;
    public const byte H264PayloadType = 101;
    public const byte OpusPayloadType = 120;

    public static byte[] Build(long sequence, long timestamp, long ssrc, bool marker, byte payloadType = H264PayloadType)
    {
        var header = new byte[Size];
        header[0] = 0x80; // version 2, no padding/extension/CSRC
        header[1] = (byte)((marker ? 0x80 : 0x00) | (payloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)(sequence & 0xFFFF));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)(timestamp & 0xFFFF_FFFF));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)(ssrc & 0xFFFF_FFFF));
        return header;
    }
}

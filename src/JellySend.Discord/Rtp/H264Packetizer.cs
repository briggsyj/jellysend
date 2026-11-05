namespace JellySend.Discord.Rtp;

/// <summary>Splits one H.264 NAL unit into RTP payloads (RFC 6184 single NAL / FU-A).</summary>
public static class H264Packetizer
{
    /// <summary>Safe UDP payload budget for Discord voice, including the RTP header.</summary>
    public const int Mtu = 1200;

    private const int FuA = 28;

    public static IReadOnlyList<byte[]> Packetize(ReadOnlySpan<byte> nal)
    {
        var maxPayload = Mtu - RtpHeader.Size;
        if (nal.IsEmpty)
        {
            return [];
        }

        if (nal.Length <= maxPayload)
        {
            return [nal.ToArray()];
        }

        var header = nal[0];
        var nalType = header & 0x1F;
        var fuIndicator = (byte)((header & 0x60) | FuA);
        var data = nal[1..];
        var payloads = new List<byte[]>();
        var first = true;
        while (!data.IsEmpty)
        {
            var take = Math.Min(maxPayload - 2, data.Length);
            var last = take == data.Length;
            var payload = new byte[take + 2];
            payload[0] = fuIndicator;
            payload[1] = (byte)((first ? 0x80 : 0) | (last ? 0x40 : 0) | nalType);
            data[..take].CopyTo(payload.AsSpan(2));
            payloads.Add(payload);
            data = data[take..];
            first = false;
        }

        return payloads;
    }
}

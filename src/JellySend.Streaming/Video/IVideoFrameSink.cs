namespace JellySend.Streaming.Video;

/// <summary>
/// Called after each RTP packet of a frame with (index, count); return false to abandon the rest of the frame
/// (stop requested mid-frame). Used to pace a frame's packets across part of the frame interval.
/// </summary>
public delegate bool PacketPacer(int index, int count);

/// <summary>Where the video player sends access units (implemented by <see cref="RtpMediaSender"/>).</summary>
public interface IVideoFrameSink
{
    /// <summary>Send one access unit; the RTP timestamp then advances by <paramref name="timestampIncrement"/>.</summary>
    void SendVideoFrame(IReadOnlyList<byte[]> nals, int timestampIncrement, PacketPacer? pacer);
}

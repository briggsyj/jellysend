using Slopsoil.Discord.Rtp;

namespace Slopsoil.Streaming;

/// <summary>
/// Splits an H.264 Annex-B byte stream into access units (one list of NAL units per frame), as
/// <c>video_player.py</c>'s read loop does: an AUD or a new slice ends the current frame, SPS/PPS/SEI
/// are carried into the next frame (SPS VUI rewritten for Discord), anything else is dropped.
/// </summary>
public sealed class AccessUnitSplitter
{
    private const int NalAud = 9;
    private const int NalSps = 7;

    private readonly List<byte> _buffer = [];
    private List<byte[]> _frame = [];
    private List<byte[]> _pending = [];

    /// <summary>Feed bytes; returns the frames completed by them.</summary>
    public IReadOnlyList<IReadOnlyList<byte[]>> Feed(ReadOnlySpan<byte> chunk)
    {
        _buffer.AddRange(chunk);
        var data = _buffer.ToArray();
        var starts = FindStartCodes(data);
        var output = new List<IReadOnlyList<byte[]>>();
        if (starts.Count < 2)
        {
            return output;
        }

        // Every NAL between two start codes is complete; the tail after the last one is kept.
        for (var i = 0; i < starts.Count - 1; i++)
        {
            var (_, end) = starts[i];
            var next = starts[i + 1].Start;
            Accept(TrimZeros(data.AsSpan(end, next - end)), output);
        }

        _buffer.Clear();
        _buffer.AddRange(data.AsSpan(starts[^1].Start));
        return output;
    }

    /// <summary>End of stream: emit the remaining NAL and frame.</summary>
    public IReadOnlyList<IReadOnlyList<byte[]>> Flush()
    {
        var output = new List<IReadOnlyList<byte[]>>();
        var data = _buffer.ToArray();
        _buffer.Clear();
        var starts = FindStartCodes(data);
        var from = starts.Count > 0 ? starts[0].End : 0;
        Accept(TrimZeros(data.AsSpan(from)), output);
        if (_frame.Count > 0)
        {
            output.Add(_frame);
            _frame = [];
        }

        return output;
    }

    private void Accept(ReadOnlySpan<byte> nal, List<IReadOnlyList<byte[]>> output)
    {
        if (nal.IsEmpty)
        {
            return;
        }

        var type = nal[0] & 0x1F;
        if (type == NalAud)
        {
            if (_frame.Count > 0)
            {
                output.Add(_frame);
                _frame = [];
            }
        }
        else if (type is 1 or 5)
        {
            if (_frame.Count > 0)
            {
                output.Add(_frame);
            }

            _frame = [.. _pending, nal.ToArray()];
            _pending = [];
        }
        else if (type is 6 or 7 or 8)
        {
            _pending.Add(type == NalSps ? SpsRewriter.Rewrite(nal) : nal.ToArray());
        }
    }

    private static ReadOnlySpan<byte> TrimZeros(ReadOnlySpan<byte> nal) => nal.TrimEnd((byte)0);

    /// <summary>(start-code offset, payload offset) for each 3- or 4-byte start code.</summary>
    private static List<(int Start, int End)> FindStartCodes(byte[] data)
    {
        var result = new List<(int, int)>();
        for (var i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                var start = i > 0 && data[i - 1] == 0 ? i - 1 : i;
                result.Add((start, i + 3));
                i += 2;
            }
        }

        return result;
    }
}

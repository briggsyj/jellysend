namespace JellySend.Discord.Rtp;

/// <summary>
/// Rewrites an H.264 SPS so <c>bitstream_restriction_flag = 1</c> and
/// <c>max_num_reorder_frames = 0</c>; Discord rejects the stream otherwise (error 2015).
/// Byte-for-byte port of <c>rewrite_sps_vui</c> in <c>streaming/video_player.py</c>.
/// </summary>
public static class SpsRewriter
{
    private static readonly HashSet<int> HighProfiles = [100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 144];

    /// <summary>Returns the rewritten SPS, or the input unchanged if it isn't an SPS or can't be parsed.</summary>
    public static byte[] Rewrite(ReadOnlySpan<byte> nal)
    {
        if (nal.IsEmpty || (nal[0] & 0x1F) != 7)
        {
            return nal.ToArray();
        }

        try
        {
            return DoRewrite(nal);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or InvalidDataException)
        {
            return nal.ToArray();
        }
    }

    private static byte[] DoRewrite(ReadOnlySpan<byte> nal)
    {
        var r = new BitReader(RemoveEmulationPrevention(nal[1..]));
        var w = new BitWriter();

        var profileIdc = r.U(8);
        w.U(8, profileIdc);
        w.U(8, r.U(8)); // constraint flags
        w.U(8, r.U(8)); // level_idc
        w.Ue(r.Ue()); // seq_parameter_set_id

        if (HighProfiles.Contains(profileIdc))
        {
            var chroma = r.Ue();
            w.Ue(chroma);
            if (chroma == 3)
            {
                w.U(1, r.U(1)); // separate_colour_plane_flag
            }

            w.Ue(r.Ue()); // bit_depth_luma_minus8
            w.Ue(r.Ue()); // bit_depth_chroma_minus8
            w.U(1, r.U(1)); // qpprime_y_zero_transform_bypass_flag
            var scalingMatrix = r.U(1);
            w.U(1, scalingMatrix);
            if (scalingMatrix == 1)
            {
                var lists = chroma == 3 ? 12 : 8;
                for (var i = 0; i < lists; i++)
                {
                    var present = r.U(1);
                    w.U(1, present);
                    if (present == 1)
                    {
                        CopyScalingList(r, w, i < 6 ? 16 : 64);
                    }
                }
            }
        }

        w.Ue(r.Ue()); // log2_max_frame_num_minus4
        var poc = r.Ue();
        w.Ue(poc);
        if (poc == 0)
        {
            w.Ue(r.Ue());
        }
        else if (poc == 1)
        {
            w.U(1, r.U(1));
            w.Se(r.Se());
            w.Se(r.Se());
            var n = r.Ue();
            w.Ue(n);
            for (var i = 0; i < n; i++)
            {
                w.Se(r.Se());
            }
        }

        var maxRefFrames = r.Ue();
        w.Ue(maxRefFrames);
        w.U(1, r.U(1)); // gaps_in_frame_num_value_allowed_flag
        w.Ue(r.Ue()); // pic_width_in_mbs_minus1
        w.Ue(r.Ue()); // pic_height_in_map_units_minus1
        var frameMbsOnly = r.U(1);
        w.U(1, frameMbsOnly);
        if (frameMbsOnly == 0)
        {
            w.U(1, r.U(1));
        }

        w.U(1, r.U(1)); // direct_8x8_inference_flag
        var cropping = r.U(1);
        w.U(1, cropping);
        if (cropping == 1)
        {
            w.Ue(r.Ue());
            w.Ue(r.Ue());
            w.Ue(r.Ue());
            w.Ue(r.Ue());
        }

        var vuiPresent = r.Remaining > 0 ? r.U(1) : 0;
        w.U(1, 1); // force vui_parameters_present_flag

        void WriteRestriction()
        {
            w.U(1, 1); // motion_vectors_over_pic_boundaries_flag
            w.Ue(2); // max_bytes_per_pic_denom
            w.Ue(1); // max_bits_per_mb_denom
            w.Ue(16);
            w.Ue(16);
            w.Ue(0); // max_num_reorder_frames = 0 (critical for Discord)
            w.Ue(maxRefFrames); // max_dec_frame_buffering
        }

        if (vuiPresent == 0)
        {
            w.U(2, 0);
            w.U(1, 0);
            w.U(5, 0);
            w.U(1, 1); // bitstream_restriction_flag
            WriteRestriction();
        }
        else
        {
            var aspect = r.U(1);
            w.U(1, aspect);
            if (aspect == 1)
            {
                var idc = r.U(8);
                w.U(8, idc);
                if (idc == 255)
                {
                    w.U(16, r.U(16));
                    w.U(16, r.U(16));
                }
            }

            var overscan = r.U(1);
            w.U(1, overscan);
            if (overscan == 1)
            {
                w.U(1, r.U(1));
            }

            var signalType = r.U(1);
            w.U(1, 0); // strip video_signal_type
            if (signalType == 1)
            {
                r.U(3);
                r.U(1);
                if (r.U(1) == 1)
                {
                    r.U(8);
                    r.U(8);
                    r.U(8);
                }
            }

            var chromaLoc = r.U(1);
            w.U(1, chromaLoc);
            if (chromaLoc == 1)
            {
                w.Ue(r.Ue());
                w.Ue(r.Ue());
            }

            var timing = r.U(1);
            w.U(1, timing);
            if (timing == 1)
            {
                w.U(32, r.U(32));
                w.U(32, r.U(32));
                w.U(1, r.U(1));
            }

            var nalHrd = r.U(1);
            w.U(1, nalHrd);
            if (nalHrd == 1)
            {
                CopyHrd(r, w);
            }

            var vclHrd = r.U(1);
            w.U(1, vclHrd);
            if (vclHrd == 1)
            {
                CopyHrd(r, w);
            }

            if (nalHrd == 1 || vclHrd == 1)
            {
                w.U(1, r.U(1)); // low_delay_hrd_flag
            }

            w.U(1, r.U(1)); // pic_struct_present_flag
            var restriction = r.U(1);
            w.U(1, 1);
            if (restriction == 0)
            {
                WriteRestriction();
            }
            else
            {
                w.U(1, r.U(1));
                w.Ue(r.Ue());
                w.Ue(r.Ue());
                w.Ue(r.Ue());
                w.Ue(r.Ue());
                r.Ue(); // original max_num_reorder_frames — discarded
                w.Ue(0);
                r.Ue(); // original max_dec_frame_buffering — discarded
                w.Ue(maxRefFrames);
            }
        }

        var body = AddEmulationPrevention(w.ToBytes());
        var result = new byte[body.Length + 1];
        result[0] = nal[0];
        body.CopyTo(result, 1);
        return result;
    }

    private static void CopyScalingList(BitReader r, BitWriter w, int size)
    {
        var last = 8;
        var next = 8;
        for (var i = 0; i < size; i++)
        {
            if (next != 0)
            {
                var delta = r.Se();
                w.Se(delta);
                next = (last + delta + 256) % 256;
            }

            last = next != 0 ? next : last;
        }
    }

    private static void CopyHrd(BitReader r, BitWriter w)
    {
        var cpb = r.Ue();
        w.Ue(cpb);
        w.U(4, r.U(4));
        w.U(4, r.U(4));
        for (var i = 0; i <= cpb; i++)
        {
            w.Ue(r.Ue());
            w.Ue(r.Ue());
            w.U(1, r.U(1));
        }

        for (var i = 0; i < 4; i++)
        {
            w.U(5, r.U(5));
        }
    }

    internal static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        var i = 0;
        while (i < data.Length)
        {
            if (i + 2 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 3)
            {
                output.Add(0);
                output.Add(0);
                i += 3;
            }
            else
            {
                output.Add(data[i]);
                i++;
            }
        }

        return [.. output];
    }

    internal static byte[] AddEmulationPrevention(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length + 8);
        var zeros = 0;
        foreach (var b in data)
        {
            if (zeros >= 2 && b <= 3)
            {
                output.Add(3);
                zeros = 0;
            }

            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return [.. output];
    }

    private sealed class BitReader(byte[] data)
    {
        private int _pos;

        public int Remaining => (data.Length * 8) - _pos;

        public int U(int n)
        {
            long value = 0;
            for (var i = 0; i < n; i++)
            {
                var byteIndex = _pos >> 3;
                if (byteIndex >= data.Length)
                {
                    throw new IndexOutOfRangeException($"SPS read past end at bit {_pos}");
                }

                value = (value << 1) | (uint)((data[byteIndex] >> (7 - (_pos & 7))) & 1);
                _pos++;
            }

            return unchecked((int)value);
        }

        public int Ue()
        {
            var zeros = 0;
            while (U(1) == 0)
            {
                zeros++;
                if (zeros > 31)
                {
                    throw new InvalidDataException("Exp-Golomb: too many leading zeros");
                }
            }

            return zeros == 0 ? 0 : (int)((1L << zeros) - 1 + (uint)U(zeros));
        }

        public int Se()
        {
            var code = Ue();
            if (code == 0)
            {
                return 0;
            }

            return (code & 1) == 1 ? (code + 1) >> 1 : -(code >> 1);
        }
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bits = [];

        public void U(int n, int value)
        {
            for (var i = n - 1; i >= 0; i--)
            {
                _bits.Add((byte)((unchecked((uint)value) >> i) & 1));
            }
        }

        public void Ue(int value)
        {
            var n = (long)value + 1;
            var length = 64 - long.LeadingZeroCount(n);
            for (var i = 0; i < length - 1; i++)
            {
                _bits.Add(0);
            }

            for (var i = (int)length - 1; i >= 0; i--)
            {
                _bits.Add((byte)((n >> i) & 1));
            }
        }

        public void Se(int value) => Ue(value > 0 ? (2 * value) - 1 : -2 * value);

        public byte[] ToBytes()
        {
            var bits = new List<byte>(_bits) { 1 };
            while (bits.Count % 8 != 0)
            {
                bits.Add(0);
            }

            var output = new byte[bits.Count / 8];
            for (var i = 0; i < output.Length; i++)
            {
                var b = 0;
                for (var j = 0; j < 8; j++)
                {
                    b = (b << 1) | bits[(i * 8) + j];
                }

                output[i] = (byte)b;
            }

            return output;
        }
    }
}

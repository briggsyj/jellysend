using Slopsoil.Discord.Rtp;

namespace Slopsoil.Tests.Discord;

public class RtpTests
{
    public static TheoryData<int> RtpCases() => [.. Enumerable.Range(0, Vectors.Section("rtp").Count())];

    public static TheoryData<string> FuaCases() => [.. Vectors.Section("fua").Select(e => e.GetProperty("name").GetString()!)];

    [Theory]
    [MemberData(nameof(RtpCases))]
    public void Header_matches_python(int index)
    {
        var v = Vectors.Section("rtp").ElementAt(index);
        var header = RtpHeader.Build(
            v.GetProperty("seq").GetInt64(), v.GetProperty("ts").GetInt64(),
            v.GetProperty("ssrc").GetInt64(), v.GetProperty("marker").GetBoolean());
        Assert.Equal(v.Hex("out"), header);
    }

    [Theory]
    [MemberData(nameof(FuaCases))]
    public void Packetize_matches_python(string name)
    {
        var v = Vectors.Section("fua").Single(e => e.GetProperty("name").GetString() == name);
        var expected = v.GetProperty("out").EnumerateArray().Select(x => Convert.FromHexString(x.GetString()!)).ToList();
        var actual = H264Packetizer.Packetize(v.Hex("in"));
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    [Fact]
    public void Packets_never_exceed_mtu()
    {
        var nal = new byte[20_000];
        nal[0] = 0x65;
        Assert.All(H264Packetizer.Packetize(nal), p => Assert.True(p.Length + RtpHeader.Size <= H264Packetizer.Mtu));
    }

    [Fact]
    public void Empty_nal_produces_no_packets() => Assert.Empty(H264Packetizer.Packetize([]));

    [Fact]
    public void Opus_payload_type_is_used_when_requested()
    {
        var header = RtpHeader.Build(1, 2, 3, marker: false, RtpHeader.OpusPayloadType);
        Assert.Equal(0x78, header[1]);
    }
}

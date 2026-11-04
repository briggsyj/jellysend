using Slopsoil.Discord.Rtp;

namespace Slopsoil.Tests.Discord;

public class SpsRewriteTests
{
    public static TheoryData<string> Cases() => [.. Vectors.Section("sps").Select(e => e.GetProperty("name").GetString()!)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rewrite_matches_python(string name)
    {
        var v = Vectors.Section("sps").Single(e => e.GetProperty("name").GetString() == name);
        Assert.Equal(v.Hex("out"), SpsRewriter.Rewrite(v.Hex("in")));
    }

    [Fact]
    public void Non_sps_is_returned_unchanged()
    {
        byte[] pps = [0x68, 0xEE, 0x3C, 0x80];
        Assert.Equal(pps, SpsRewriter.Rewrite(pps));
        Assert.Empty(SpsRewriter.Rewrite([]));
    }

    [Fact]
    public void Truncated_sps_is_returned_unchanged()
    {
        byte[] truncated = [0x67, 0x64];
        Assert.Equal(truncated, SpsRewriter.Rewrite(truncated));
    }

    [Fact]
    public void Emulation_prevention_round_trips()
    {
        byte[] raw = [0x00, 0x00, 0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0x03];
        var escaped = SpsRewriter.AddEmulationPrevention(raw);
        Assert.Equal([0x00, 0x00, 0x03, 0x01, 0x05, 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x03], escaped);
        Assert.Equal(raw, SpsRewriter.RemoveEmulationPrevention(escaped));
    }
}

using Slopsoil.Discord.Native;
using Slopsoil.Discord.Rtp;

namespace Slopsoil.Tests.Discord;

public class TransportCryptoTests
{
    public static TheoryData<int> Cases() => [.. Enumerable.Range(0, Vectors.Section("crypto").Count())];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Encrypt_matches_python(int index)
    {
        var v = Vectors.Section("crypto").ElementAt(index);
        var nonce = v.GetProperty("nonce").GetUInt32();
        var output = TransportCrypto.Encrypt(
            v.Hex("header"), v.Hex("payload"), v.GetProperty("mode").GetString()!, v.Hex("key"), ref nonce);
        Assert.Equal(v.Hex("out"), output);
        Assert.Equal(v.GetProperty("next_nonce").GetUInt32(), nonce);
    }

    [Fact]
    public void Counter_nonce_wraps_at_32_bits()
    {
        var nonce = uint.MaxValue;
        TransportCrypto.Encrypt(new byte[12], [1, 2, 3], TransportCrypto.XSalsaLite, new byte[32], ref nonce);
        Assert.Equal(0u, nonce);
    }

    [Fact]
    public void Unknown_mode_is_rejected()
    {
        var nonce = 0u;
        Assert.Throws<ArgumentException>(() =>
            TransportCrypto.Encrypt(new byte[12], [1], "aes256_gcm", new byte[32], ref nonce));
    }

    [Fact]
    public void Bad_key_length_is_rejected()
    {
        var nonce = 0u;
        Assert.Throws<ArgumentException>(() =>
            TransportCrypto.Encrypt(new byte[12], [1], TransportCrypto.XChaChaRtpSize, new byte[16], ref nonce));
    }

    [Fact]
    public void Random_bytes_fills_buffer()
    {
        var buffer = new byte[32];
        Sodium.RandomBytes(buffer);
        Assert.Contains(buffer, b => b != 0);
    }

    [Fact]
    public void Opus_encodes_a_silent_frame()
    {
        using var encoder = new OpusEncoder();
        var packet = encoder.Encode(new byte[OpusEncoder.FrameBytes]);
        Assert.InRange(packet.Length, 1, 4000);
        Assert.Throws<ArgumentException>(() => encoder.Encode(new byte[10]));
    }
}

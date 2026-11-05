using System.Buffers.Binary;
using JellySend.Discord.Native;

namespace JellySend.Discord.Rtp;

/// <summary>Discord voice transport encryption (what wraps each RTP packet on UDP).</summary>
public static class TransportCrypto
{
    public const string XChaChaRtpSize = "aead_xchacha20_poly1305_rtpsize";
    public const string XSalsaLite = "xsalsa20_poly1305_lite";
    public const string XSalsa = "xsalsa20_poly1305";

    /// <summary>
    /// Encrypt <paramref name="payload"/> after <paramref name="header"/> for <paramref name="mode"/>.
    /// Counter-nonce modes use and advance <paramref name="nonce"/> (wrapping at 2^32).
    /// </summary>
    public static byte[] Encrypt(
        ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, string mode, ReadOnlySpan<byte> key, ref uint nonce)
    {
        switch (mode)
        {
            case XChaChaRtpSize:
            {
                Span<byte> n = stackalloc byte[Sodium.XChaChaNonceBytes];
                n.Clear();
                BinaryPrimitives.WriteUInt32BigEndian(n, nonce);
                nonce = unchecked(nonce + 1);
                var ct = Sodium.AeadXChaChaEncrypt(payload, header, n, key);
                return Concat(header, ct, n[..4]);
            }

            case XSalsaLite:
            {
                Span<byte> n = stackalloc byte[Sodium.SecretBoxNonceBytes];
                n.Clear();
                BinaryPrimitives.WriteUInt32BigEndian(n, nonce);
                nonce = unchecked(nonce + 1);
                var ct = Sodium.SecretBox(payload, n, key);
                return Concat(header, ct, n[..4]);
            }

            case XSalsa:
            {
                Span<byte> n = stackalloc byte[Sodium.SecretBoxNonceBytes];
                n.Clear();
                header[..Math.Min(12, header.Length)].CopyTo(n);
                return Concat(header, Sodium.SecretBox(payload, n, key), []);
            }

            default:
                throw new ArgumentException($"unknown voice encryption mode: {mode}", nameof(mode));
        }
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        c.CopyTo(result.AsSpan(a.Length + b.Length));
        return result;
    }
}

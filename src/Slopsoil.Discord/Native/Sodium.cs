using System.Runtime.InteropServices;

namespace Slopsoil.Discord.Native;

/// <summary>Minimal libsodium bindings: the two AEAD/secretbox primitives Discord voice needs.</summary>
internal static partial class Sodium
{
    private const string Lib = "sodium";

    public const int XChaChaKeyBytes = 32;
    public const int XChaChaNonceBytes = 24;
    public const int XChaChaABytes = 16;
    public const int SecretBoxKeyBytes = 32;
    public const int SecretBoxNonceBytes = 24;
    public const int SecretBoxMacBytes = 16;

    private static readonly int Init = sodium_init();

    [LibraryImport(Lib)]
    private static partial int sodium_init();

    [LibraryImport(Lib)]
    private static unsafe partial int crypto_aead_xchacha20poly1305_ietf_encrypt(
        byte* c, out ulong clen, byte* m, ulong mlen, byte* ad, ulong adlen,
        byte* nsec, byte* npub, byte* k);

    [LibraryImport(Lib)]
    private static unsafe partial int crypto_secretbox_easy(
        byte* c, byte* m, ulong mlen, byte* n, byte* k);

    [LibraryImport(Lib)]
    private static unsafe partial void randombytes_buf(byte* buf, nuint size);

    private static void EnsureInit()
    {
        if (Init < 0)
        {
            throw new InvalidOperationException("libsodium failed to initialise");
        }
    }

    /// <summary>XChaCha20-Poly1305 (IETF) encrypt. Returns ciphertext ‖ tag.</summary>
    public static unsafe byte[] AeadXChaChaEncrypt(
        ReadOnlySpan<byte> message, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> key)
    {
        EnsureInit();
        if (nonce.Length != XChaChaNonceBytes || key.Length != XChaChaKeyBytes)
        {
            throw new ArgumentException("bad nonce/key length");
        }

        var output = new byte[message.Length + XChaChaABytes];
        fixed (byte* c = output)
        fixed (byte* m = message)
        fixed (byte* a = ad)
        fixed (byte* n = nonce)
        fixed (byte* k = key)
        {
            if (crypto_aead_xchacha20poly1305_ietf_encrypt(
                    c, out var clen, m, (ulong)message.Length, a, (ulong)ad.Length, null, n, k) != 0
                || clen != (ulong)output.Length)
            {
                throw new InvalidOperationException("xchacha20poly1305 encrypt failed");
            }
        }

        return output;
    }

    /// <summary>XSalsa20-Poly1305 secretbox. Returns tag ‖ ciphertext (NaCl "easy" layout).</summary>
    public static unsafe byte[] SecretBox(ReadOnlySpan<byte> message, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> key)
    {
        EnsureInit();
        if (nonce.Length != SecretBoxNonceBytes || key.Length != SecretBoxKeyBytes)
        {
            throw new ArgumentException("bad nonce/key length");
        }

        var output = new byte[message.Length + SecretBoxMacBytes];
        fixed (byte* c = output)
        fixed (byte* m = message)
        fixed (byte* n = nonce)
        fixed (byte* k = key)
        {
            if (crypto_secretbox_easy(c, m, (ulong)message.Length, n, k) != 0)
            {
                throw new InvalidOperationException("secretbox encrypt failed");
            }
        }

        return output;
    }

    public static unsafe void RandomBytes(Span<byte> buffer)
    {
        EnsureInit();
        fixed (byte* b = buffer)
        {
            randombytes_buf(b, (nuint)buffer.Length);
        }
    }
}

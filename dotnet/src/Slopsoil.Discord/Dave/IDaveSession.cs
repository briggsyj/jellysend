namespace Slopsoil.Discord.Dave;

/// <summary>
/// One DAVE (Discord audio/video end-to-end encryption) group session for a voice or go-live
/// connection. Mirrors the surface the Python bot uses (<c>davey_compat.DaveSession</c>), backed in
/// production by libdave's C API (<see cref="LibDaveSession"/>) and by fakes in tests.
/// </summary>
public interface IDaveSession : IDisposable
{
    /// <summary>Negotiated DAVE protocol version (0 = DAVE disabled / passthrough).</summary>
    int ProtocolVersion { get; }

    /// <summary>True once a key ratchet is installed and media can be encrypted.</summary>
    bool Ready { get; }

    /// <summary>(Re)initialise for <paramref name="protocolVersion"/> in <paramref name="channelId"/> as <paramref name="userId"/>.</summary>
    void Reinit(int protocolVersion, ulong userId, ulong channelId);

    void Reset();

    /// <summary>MLS key package to send as voice opcode 26.</summary>
    byte[] GetSerializedKeyPackage();

    /// <summary>External sender from voice opcode 25.</summary>
    void SetExternalSender(ReadOnlySpan<byte> data);

    /// <summary>Proposals from opcode 27; returns a commit(+welcome) to send as opcode 28, or null.</summary>
    byte[]? ProcessProposals(DaveProposalsOperation operation, ReadOnlySpan<byte> proposals, IReadOnlyCollection<ulong> recognizedUserIds);

    /// <summary>Commit from opcode 29. Returns false if it failed (caller sends opcode 31 invalid commit/welcome).</summary>
    bool ProcessCommit(ReadOnlySpan<byte> commit);

    /// <summary>Welcome from opcode 30. Returns false on failure.</summary>
    bool ProcessWelcome(ReadOnlySpan<byte> welcome, IReadOnlyCollection<ulong> recognizedUserIds);

    void SetPassthroughMode(bool passthrough);

    /// <summary>Encrypt one Opus frame for the audio SSRC (passthrough when not ready).</summary>
    byte[] EncryptOpus(uint ssrc, ReadOnlySpan<byte> frame);

    /// <summary>Register the video SSRC as H.264 (must precede <see cref="EncryptH264"/>).</summary>
    void RegisterVideoSsrc(uint ssrc);

    /// <summary>Encrypt one complete Annex-B access unit for the video SSRC.</summary>
    byte[] EncryptH264(uint ssrc, ReadOnlySpan<byte> accessUnit);
}

public enum DaveProposalsOperation
{
    Append = 0,
    Revoke = 1,
}

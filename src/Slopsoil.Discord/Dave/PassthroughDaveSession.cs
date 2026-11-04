namespace Slopsoil.Discord.Dave;

/// <summary>
/// <see cref="IDaveSession"/> for connections without DAVE (protocol version 0): media passes through
/// unchanged and MLS operations are inert.
/// </summary>
public sealed class PassthroughDaveSession : IDaveSession
{
    public int ProtocolVersion => 0;

    public bool Ready => false;

    public void Reinit(int protocolVersion, ulong userId, ulong channelId)
    {
    }

    public void Reset()
    {
    }

    public byte[] GetSerializedKeyPackage() => [];

    public void SetExternalSender(ReadOnlySpan<byte> data)
    {
    }

    public byte[]? ProcessProposals(DaveProposalsOperation operation, ReadOnlySpan<byte> proposals, IReadOnlyCollection<ulong> recognizedUserIds) => null;

    public bool? ProcessCommit(ReadOnlySpan<byte> commit) => false;

    public bool ProcessWelcome(ReadOnlySpan<byte> welcome, IReadOnlyCollection<ulong> recognizedUserIds) => false;

    public void SetPassthroughMode(bool passthrough)
    {
    }

    public byte[] EncryptOpus(uint ssrc, ReadOnlySpan<byte> frame) => frame.ToArray();

    public void RegisterVideoSsrc(uint ssrc)
    {
    }

    public byte[] EncryptH264(uint ssrc, ReadOnlySpan<byte> accessUnit) => accessUnit.ToArray();

    public void Dispose()
    {
    }
}

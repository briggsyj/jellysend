using System.Runtime.InteropServices;
using JellySend.Discord.Dave;
using JellySend.Discord.Native;

namespace JellySend.Tests.Discord;

public class DaveSessionTests
{
    private const ulong Self = 1001;
    private const ulong Channel = 555;

    private static (LibDaveSession Session, FakeLibDave Dave, List<string> Log) Create(int version = 1)
    {
        var dave = new FakeLibDave();
        var log = new List<string>();
        return (new LibDaveSession(dave, version, Self, Channel, log.Add), dave, log);
    }

    private static LibDaveSession Ready(FakeLibDave dave)
    {
        var session = new LibDaveSession(dave, 1, Self, Channel);
        Assert.True(session.ProcessCommit([1]));
        Assert.True(session.Ready);
        return session;
    }

    [Fact]
    public void Construction_initialises_group_as_channel_and_starts_in_passthrough()
    {
        var (session, dave, _) = Create();
        Assert.Equal(1, session.ProtocolVersion);
        Assert.False(session.Ready);
        Assert.Equal([(1, Channel, "1001")], dave.Session.Inits);
        Assert.Equal([true], dave.Encryptor.Passthrough);
    }

    [Fact]
    public void Version_zero_skips_mls_init()
    {
        var (session, dave, _) = Create(version: 0);
        Assert.Equal(0, session.ProtocolVersion);
        Assert.Empty(dave.Session.Inits);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65_536)]
    public void Out_of_range_version_is_rejected(int version)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LibDaveSession(new FakeLibDave(), version, Self, Channel));
        using var session = new LibDaveSession(new FakeLibDave(), 1, Self, Channel);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Reinit(version, Self, Channel));
    }

    [Fact]
    public void Encryptor_failure_disposes_the_session()
    {
        var dave = new FakeLibDave { FailEncryptor = true };
        Assert.Throws<InvalidOperationException>(() => new LibDaveSession(dave, 1, Self, Channel));
        Assert.True(dave.Session.Disposed);
    }

    [Fact]
    public void Key_package_and_external_sender_delegate_to_mls()
    {
        var (session, dave, _) = Create();
        dave.Session.KeyPackage = [9, 8, 7];
        Assert.Equal([9, 8, 7], session.GetSerializedKeyPackage());
        session.SetExternalSender([1, 2]);
        Assert.Equal([1, 2], dave.Session.ExternalSender);
    }

    [Theory]
    [InlineData(DaveProposalsOperation.Append, 0)]
    [InlineData(DaveProposalsOperation.Revoke, 1)]
    public void Proposals_get_the_operation_byte_prepended(DaveProposalsOperation operation, byte expected)
    {
        var (session, dave, _) = Create();
        dave.Session.CommitWelcome = [0xAA, 0xBB];
        var result = session.ProcessProposals(operation, [5, 6], [2002, 3003]);
        Assert.Equal([0xAA, 0xBB], result);
        Assert.Equal([expected, 5, 6], dave.Session.Proposals);
    }

    [Fact]
    public void Recognized_users_always_include_self_once()
    {
        var (session, dave, _) = Create();
        session.ProcessProposals(DaveProposalsOperation.Append, [], [3003, Self, 2002]);
        Assert.Equal(["1001", "2002", "3003"], dave.Session.Recognized);
        session.ProcessWelcome([1], []);
        Assert.Equal(["1001"], dave.Session.Recognized);
    }

    [Fact]
    public void Empty_commit_welcome_means_nothing_to_send()
    {
        var (session, _, _) = Create();
        Assert.Null(session.ProcessProposals(DaveProposalsOperation.Append, [1], []));
    }

    [Fact]
    public void Successful_commit_installs_own_ratchet()
    {
        var (session, dave, _) = Create();
        Assert.True(session.ProcessCommit([1]));
        Assert.True(session.Ready);
        Assert.Equal(["1001"], dave.Session.RatchetRequests);
        Assert.Same(dave.Session.Ratchets[0], dave.Encryptor.Ratchet);
        Assert.Equal([true, false], dave.Encryptor.Passthrough);
    }

    [Fact]
    public void Failed_commit_returns_false_and_stays_in_passthrough()
    {
        var (session, dave, log) = Create();
        dave.Session.CommitOutcome = DaveCommitOutcome.Failed;
        Assert.False(session.ProcessCommit([1]));
        Assert.False(session.Ready);
        Assert.Empty(dave.Session.RatchetRequests);
        Assert.Contains(log, l => l.Contains("commit rejected"));
    }

    [Fact]
    public void Ignored_commit_is_not_a_failure_and_keeps_the_key()
    {
        var (session, dave, _) = Create();
        dave.Session.CommitOutcome = DaveCommitOutcome.Ignored;
        Assert.Null(session.ProcessCommit([1]));
        Assert.False(session.Ready);
        Assert.Empty(dave.Session.RatchetRequests);
    }

    [Fact]
    public void Missing_ratchet_leaves_session_not_ready()
    {
        var (session, dave, log) = Create();
        dave.Session.NoRatchet = true;
        Assert.True(session.ProcessCommit([1]));
        Assert.False(session.Ready);
        Assert.Null(dave.Encryptor.Ratchet);
        Assert.Contains(log, l => l.Contains("no key ratchet"));
    }

    [Fact]
    public void New_ratchet_replaces_old_before_old_is_released()
    {
        var dave = new FakeLibDave();
        using var session = Ready(dave);
        var first = dave.Session.Ratchets[0];
        Assert.True(session.ProcessWelcome([1], []));
        var second = dave.Session.Ratchets[1];
        Assert.True(first.Disposed);
        Assert.False(first.DisposedWhileInstalled);
        Assert.False(second.Disposed);
        Assert.Same(second, dave.Encryptor.Ratchet);
    }

    [Fact]
    public void Failed_welcome_returns_false()
    {
        var (session, dave, log) = Create();
        dave.Session.WelcomeOk = false;
        Assert.False(session.ProcessWelcome([1], [2002]));
        Assert.False(session.Ready);
        Assert.Contains(log, l => l.Contains("welcome rejected"));
    }

    [Fact]
    public void Encrypt_before_ready_passes_through_without_libdave()
    {
        var (session, dave, _) = Create();
        Assert.Equal([1, 2, 3], session.EncryptOpus(42, [1, 2, 3]));
        Assert.Equal([4, 5], session.EncryptH264(43, [4, 5]));
        Assert.Equal(0, dave.Encryptor.EncryptCalls);
    }

    [Fact]
    public void Encrypt_uses_max_ciphertext_capacity_and_trims_output()
    {
        var dave = new FakeLibDave();
        using var session = Ready(dave);
        var output = session.EncryptOpus(42, [1, 2, 3]);
        Assert.Equal([0xDA, 1, 2, 3, 0xFA], output);
        Assert.Equal((DaveMediaType.Audio, 42u, 3 + FakeEncryptor.Overhead), dave.Encryptor.LastCall);

        var video = session.EncryptH264(43, new byte[2000]);
        Assert.Equal(2002, video.Length);
        Assert.Equal((DaveMediaType.Video, 43u, 2000 + FakeEncryptor.Overhead), dave.Encryptor.LastCall);
    }

    [Theory]
    [InlineData(1, 0)] // ENCRYPTION_FAILURE
    [InlineData(2, 0)] // MISSING_KEY_RATCHET
    [InlineData(0, -1)] // SUCCESS with a nonsensical length
    [InlineData(0, 1000)] // SUCCESS claiming more than the capacity
    public void Encrypt_failure_falls_back_to_plaintext(int result, int written)
    {
        var dave = new FakeLibDave();
        using var session = Ready(dave);
        dave.Encryptor.ForcedResult = ((DaveEncryptorResult)result, written);
        Assert.Equal([7, 7], session.EncryptOpus(1, [7, 7]));
        Assert.Equal(1, session.EncryptFailures);
    }

    [Fact]
    public void Encrypting_an_empty_frame_works()
    {
        var dave = new FakeLibDave();
        using var session = Ready(dave);
        dave.Encryptor.MaxOverhead = 0;
        Assert.Empty(session.EncryptOpus(1, []));
    }

    [Fact]
    public void Ssrcs_are_assigned_their_codec_once()
    {
        var (session, dave, _) = Create();
        session.RegisterVideoSsrc(43);
        session.RegisterVideoSsrc(43);
        session.EncryptH264(43, [1]);
        session.EncryptOpus(42, [1]);
        session.EncryptOpus(42, [1]);
        session.EncryptH264(44, [1]);
        Assert.Equal([(43u, DaveCodec.H264), (42u, DaveCodec.Opus), (44u, DaveCodec.H264)], dave.Encryptor.Codecs);
    }

    [Fact]
    public void Reset_drops_the_key_and_returns_to_passthrough()
    {
        var dave = new FakeLibDave();
        using var session = Ready(dave);
        session.Reset();
        Assert.False(session.Ready);
        Assert.Equal(1, dave.Session.Resets);
        Assert.Null(dave.Encryptor.Ratchet);
        Assert.True(dave.Session.Ratchets[0].Disposed);
        Assert.False(dave.Session.Ratchets[0].DisposedWhileInstalled);
        Assert.True(dave.Encryptor.Passthrough[^1]);
        Assert.Equal([9], session.EncryptOpus(1, [9]));
    }

    [Fact]
    public void Reinit_resets_then_initialises_with_new_parameters()
    {
        var dave = new FakeLibDave();
        using var session = Ready(dave);
        session.Reinit(2, 7, 8);
        Assert.Equal(2, session.ProtocolVersion);
        Assert.False(session.Ready);
        Assert.Equal(1, dave.Session.Resets);
        Assert.Equal((2, 8ul, "7"), dave.Session.Inits[^1]);
        Assert.True(session.ProcessCommit([1]));
        Assert.Equal("7", dave.Session.RatchetRequests[^1]);
    }

    [Fact]
    public void Passthrough_mode_is_forwarded()
    {
        var (session, dave, _) = Create();
        session.SetPassthroughMode(false);
        Assert.False(dave.Encryptor.Passthrough[^1]);
    }

    [Fact]
    public void Mls_failures_are_logged()
    {
        var (session, dave, log) = Create();
        dave.Session.RaiseFailure("ProcessCommit", "bad epoch");
        Assert.Equal(["[DAVE] MLS failure: ProcessCommit – bad epoch"], log);
        Assert.Equal(1, session.ProtocolVersion);
    }

    [Fact]
    public void Dispose_releases_everything_once()
    {
        var dave = new FakeLibDave();
        var session = Ready(dave);
        session.Dispose();
        session.Dispose();
        Assert.True(dave.Session.Disposed);
        Assert.True(dave.Encryptor.Disposed);
        Assert.True(dave.Session.Ratchets[0].Disposed);
        Assert.Throws<ObjectDisposedException>(() => session.EncryptOpus(1, [1]));
        Assert.Throws<ObjectDisposedException>(() => session.GetSerializedKeyPackage());
        Assert.Throws<ObjectDisposedException>(() => session.ProcessCommit([1]));
        Assert.Throws<ObjectDisposedException>(() => session.Reset());
    }

    [Fact]
    public void Production_session_uses_libdave_when_present()
    {
        if (!NativeLibrary.TryLoad("libdave.so", typeof(LibDaveSession).Assembly, null, out var lib))
        {
            Assert.Throws<DllNotFoundException>(() => new LibDaveSession(1, Self, Channel));
            Assert.Throws<DllNotFoundException>(() => LibDaveSession.MaxSupportedProtocolVersion);
            return;
        }

        NativeLibrary.Free(lib);
        using var session = new LibDaveSession(LibDaveSession.MaxSupportedProtocolVersion, Self, Channel);
        Assert.NotEmpty(session.GetSerializedKeyPackage());
        Assert.Equal([1, 2, 3], session.EncryptOpus(1, [1, 2, 3]));
    }

    [Fact]
    public void Passthrough_session_returns_media_unchanged()
    {
        IDaveSession session = new PassthroughDaveSession();
        session.Reinit(1, Self, Channel);
        session.Reset();
        session.SetExternalSender([1]);
        session.SetPassthroughMode(false);
        session.RegisterVideoSsrc(1);
        Assert.Equal(0, session.ProtocolVersion);
        Assert.False(session.Ready);
        Assert.Empty(session.GetSerializedKeyPackage());
        Assert.Null(session.ProcessProposals(DaveProposalsOperation.Append, [1], []));
        Assert.False(session.ProcessCommit([1]));
        Assert.False(session.ProcessWelcome([1], []));
        Assert.Equal([1, 2], session.EncryptOpus(1, [1, 2]));
        Assert.Equal([3, 4], session.EncryptH264(2, [3, 4]));
        session.Dispose();
    }

    [Fact]
    public unsafe void TakeBytes_copies_and_frees_native_buffers()
    {
        var freed = new List<IntPtr>();
        var ptr = Marshal.AllocHGlobal(3);
        new Span<byte>((void*)ptr, 3).Fill(0x5A);
        Assert.Equal([0x5A, 0x5A, 0x5A], DaveMarshal.TakeBytes(ptr, 3, p => { freed.Add(p); Marshal.FreeHGlobal(p); }));
        Assert.Equal([ptr], freed);

        var empty = Marshal.AllocHGlobal(1);
        Assert.Empty(DaveMarshal.TakeBytes(empty, 0, p => { freed.Add(p); Marshal.FreeHGlobal(p); }));
        Assert.Equal(empty, freed[^1]);

        Assert.Empty(DaveMarshal.TakeBytes(IntPtr.Zero, 5, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void TakeBytes_frees_even_when_length_overflows()
    {
        var ptr = Marshal.AllocHGlobal(1);
        var freed = false;
        Assert.Throws<OverflowException>(() => DaveMarshal.TakeBytes(ptr, nuint.MaxValue, p => { freed = true; Marshal.FreeHGlobal(p); }));
        Assert.True(freed);
    }

    [Fact]
    public unsafe void Utf8StringArray_builds_nul_terminated_strings()
    {
        var array = new DaveMarshal.Utf8StringArray(["1001", "ü"]);
        Assert.Equal(2u, array.Count);
        Assert.Equal("1001", Marshal.PtrToStringUTF8((IntPtr)array.Pointer[0]));
        Assert.Equal("ü", Marshal.PtrToStringUTF8((IntPtr)array.Pointer[1]));
        array.Dispose();
        array.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = array.Pointer; });

        using var none = new DaveMarshal.Utf8StringArray([]);
        Assert.Equal(0u, none.Count);
        Assert.True(none.Pointer != null);
    }

    private sealed class FakeLibDave : ILibDave
    {
        public bool FailEncryptor { get; init; }

        public FakeMlsSession Session { get; private set; } = null!;

        public FakeEncryptor Encryptor { get; private set; } = null!;

        public ushort MaxSupportedProtocolVersion => 1;

        public IDaveMlsSession CreateSession(Action<string, string> onMlsFailure) => Session = new FakeMlsSession(onMlsFailure);

        public IDaveEncryptor CreateEncryptor()
        {
            if (FailEncryptor)
            {
                throw new InvalidOperationException("no encryptor");
            }

            Encryptor = new FakeEncryptor();
            Session.Encryptor = Encryptor;
            return Encryptor;
        }
    }

    private sealed class FakeMlsSession(Action<string, string> onMlsFailure) : IDaveMlsSession
    {
        public List<(int Version, ulong Group, string User)> Inits { get; } = [];

        public int Resets { get; private set; }

        public byte[] KeyPackage { get; set; } = [];

        public byte[] ExternalSender { get; private set; } = [];

        public byte[] Proposals { get; private set; } = [];

        public byte[] CommitWelcome { get; set; } = [];

        public IReadOnlyList<string> Recognized { get; private set; } = [];

        public DaveCommitOutcome CommitOutcome { get; set; } = DaveCommitOutcome.Success;

        public bool WelcomeOk { get; set; } = true;

        public bool NoRatchet { get; set; }

        public List<string> RatchetRequests { get; } = [];

        public List<FakeRatchet> Ratchets { get; } = [];

        public bool Disposed { get; private set; }

        public FakeEncryptor? Encryptor { get; set; }

        public void RaiseFailure(string source, string reason) => onMlsFailure(source, reason);

        public void Init(ushort version, ulong groupId, string selfUserId) => Inits.Add((version, groupId, selfUserId));

        public void Reset() => Resets++;

        public byte[] GetMarshalledKeyPackage() => KeyPackage;

        public void SetExternalSender(ReadOnlySpan<byte> externalSender) => ExternalSender = externalSender.ToArray();

        public byte[] ProcessProposals(ReadOnlySpan<byte> proposals, IReadOnlyList<string> recognizedUserIds)
        {
            Proposals = proposals.ToArray();
            Recognized = recognizedUserIds;
            return CommitWelcome;
        }

        public DaveCommitOutcome ProcessCommit(ReadOnlySpan<byte> commit) => CommitOutcome;

        public bool ProcessWelcome(ReadOnlySpan<byte> welcome, IReadOnlyList<string> recognizedUserIds)
        {
            Recognized = recognizedUserIds;
            return WelcomeOk;
        }

        public IDaveKeyRatchet? GetKeyRatchet(string userId)
        {
            RatchetRequests.Add(userId);
            if (NoRatchet)
            {
                return null;
            }

            var ratchet = new FakeRatchet(this);
            Ratchets.Add(ratchet);
            return ratchet;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeRatchet(FakeMlsSession owner) : IDaveKeyRatchet
    {
        public bool Disposed { get; private set; }

        public bool DisposedWhileInstalled { get; private set; }

        public void Dispose()
        {
            DisposedWhileInstalled |= ReferenceEquals(owner.Encryptor?.Ratchet, this) && owner.Encryptor?.Disposed == false;
            Disposed = true;
        }
    }

    private sealed class FakeEncryptor : IDaveEncryptor
    {
        public const int Overhead = 10;

        public int MaxOverhead { get; set; } = Overhead;

        public IDaveKeyRatchet? Ratchet { get; private set; }

        public List<bool> Passthrough { get; } = [];

        public List<(uint Ssrc, DaveCodec Codec)> Codecs { get; } = [];

        public int EncryptCalls { get; private set; }

        public (DaveMediaType Type, uint Ssrc, int Capacity) LastCall { get; private set; }

        public (DaveEncryptorResult Result, int Written)? ForcedResult { get; set; }

        public bool Disposed { get; private set; }

        public void SetKeyRatchet(IDaveKeyRatchet? keyRatchet) => Ratchet = keyRatchet;

        public void SetPassthroughMode(bool passthrough) => Passthrough.Add(passthrough);

        public void AssignSsrcToCodec(uint ssrc, DaveCodec codec) => Codecs.Add((ssrc, codec));

        public int GetMaxCiphertextByteSize(DaveMediaType mediaType, int frameSize) => frameSize + MaxOverhead;

        public DaveEncryptorResult Encrypt(DaveMediaType mediaType, uint ssrc, ReadOnlySpan<byte> frame, Span<byte> output, out int written)
        {
            EncryptCalls++;
            LastCall = (mediaType, ssrc, output.Length);
            if (ForcedResult is { } forced)
            {
                written = forced.Written;
                return forced.Result;
            }

            if (MaxOverhead == 0)
            {
                written = frame.Length;
                frame.CopyTo(output);
                return DaveEncryptorResult.Success;
            }

            output[0] = 0xDA;
            frame.CopyTo(output[1..]);
            output[frame.Length + 1] = 0xFA;
            written = frame.Length + 2;
            return DaveEncryptorResult.Success;
        }

        public void Dispose() => Disposed = true;
    }
}

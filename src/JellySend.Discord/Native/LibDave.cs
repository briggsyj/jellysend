using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using JellySend.Discord.Dave;

namespace JellySend.Discord.Native;

/// <summary><c>DAVECodec</c> from <c>dave.h</c>.</summary>
internal enum DaveCodec
{
    Unknown = 0,
    Opus = 1,
    Vp8 = 2,
    Vp9 = 3,
    H264 = 4,
    H265 = 5,
    Av1 = 6,
}

/// <summary><c>DAVEMediaType</c> from <c>dave.h</c>.</summary>
internal enum DaveMediaType
{
    Audio = 0,
    Video = 1,
}

/// <summary><c>DAVEEncryptorResultCode</c> from <c>dave.h</c>.</summary>
internal enum DaveEncryptorResult
{
    Success = 0,
    EncryptionFailure = 1,
    MissingKeyRatchet = 2,
    MissingCryptor = 3,
    TooManyAttempts = 4,
}

/// <summary>Outcome of <c>daveSessionProcessCommit</c>.</summary>
internal enum DaveCommitOutcome
{
    Success,
    Failed,
    Ignored,
}

/// <summary>
/// Managed view of libdave's C API. The production implementation is <see cref="LibDaveNative"/>;
/// tests substitute a fake so <see cref="LibDaveSession"/> can be exercised without the native library.
/// </summary>
internal interface ILibDave
{
    ushort MaxSupportedProtocolVersion { get; }

    /// <summary><c>daveSessionCreate</c>; <paramref name="onMlsFailure"/> receives (source, reason).</summary>
    IDaveMlsSession CreateSession(Action<string, string> onMlsFailure);

    IDaveEncryptor CreateEncryptor();
}

/// <summary>A <c>DAVESessionHandle</c>.</summary>
internal interface IDaveMlsSession : IDisposable
{
    void Init(ushort version, ulong groupId, string selfUserId);

    void Reset();

    byte[] GetMarshalledKeyPackage();

    void SetExternalSender(ReadOnlySpan<byte> externalSender);

    /// <summary>Returns the commit(+welcome) bytes, or an empty array when there is nothing to send.</summary>
    byte[] ProcessProposals(ReadOnlySpan<byte> proposals, IReadOnlyList<string> recognizedUserIds);

    DaveCommitOutcome ProcessCommit(ReadOnlySpan<byte> commit);

    /// <summary>False when libdave rejected the welcome (null result handle).</summary>
    bool ProcessWelcome(ReadOnlySpan<byte> welcome, IReadOnlyList<string> recognizedUserIds);

    /// <summary>Null when the session has no ratchet for <paramref name="userId"/> yet.</summary>
    IDaveKeyRatchet? GetKeyRatchet(string userId);
}

/// <summary>A <c>DAVEKeyRatchetHandle</c>.</summary>
internal interface IDaveKeyRatchet : IDisposable;

/// <summary>A <c>DAVEEncryptorHandle</c>.</summary>
internal interface IDaveEncryptor : IDisposable
{
    /// <summary>The encryptor does not take ownership: the ratchet must outlive its use.</summary>
    void SetKeyRatchet(IDaveKeyRatchet? keyRatchet);

    void SetPassthroughMode(bool passthrough);

    void AssignSsrcToCodec(uint ssrc, DaveCodec codec);

    int GetMaxCiphertextByteSize(DaveMediaType mediaType, int frameSize);

    DaveEncryptorResult Encrypt(DaveMediaType mediaType, uint ssrc, ReadOnlySpan<byte> frame, Span<byte> output, out int written);
}

/// <summary>Raw <c>dave.h</c> entry points (only the encrypt side; jellysend never decrypts).</summary>
internal static unsafe partial class DaveNative
{
    private const string Lib = "libdave.so";

    [LibraryImport(Lib)]
    internal static partial ushort daveMaxSupportedProtocolVersion();

    [LibraryImport(Lib)]
    internal static partial void daveSetLogSinkCallback(delegate* unmanaged[Cdecl]<int, byte*, int, byte*, void> callback);

    [LibraryImport(Lib)]
    internal static partial void daveFree(void* ptr);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DaveSessionSafeHandle daveSessionCreate(
        void* context, string? authSessionId,
        delegate* unmanaged[Cdecl]<byte*, byte*, void*, void> callback, void* userData);

    [LibraryImport(Lib)]
    internal static partial void daveSessionDestroy(IntPtr session);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void daveSessionInit(DaveSessionSafeHandle session, ushort version, ulong groupId, string selfUserId);

    [LibraryImport(Lib)]
    internal static partial void daveSessionReset(DaveSessionSafeHandle session);

    [LibraryImport(Lib)]
    internal static partial void daveSessionSetExternalSender(DaveSessionSafeHandle session, byte* externalSender, nuint length);

    [LibraryImport(Lib)]
    internal static partial void daveSessionProcessProposals(
        DaveSessionSafeHandle session, byte* proposals, nuint length,
        byte** recognizedUserIds, nuint recognizedUserIdsLength,
        byte** commitWelcomeBytes, nuint* commitWelcomeBytesLength);

    [LibraryImport(Lib)]
    internal static partial DaveCommitResultSafeHandle daveSessionProcessCommit(DaveSessionSafeHandle session, byte* commit, nuint length);

    [LibraryImport(Lib)]
    internal static partial DaveWelcomeResultSafeHandle daveSessionProcessWelcome(
        DaveSessionSafeHandle session, byte* welcome, nuint length,
        byte** recognizedUserIds, nuint recognizedUserIdsLength);

    [LibraryImport(Lib)]
    internal static partial void daveSessionGetMarshalledKeyPackage(DaveSessionSafeHandle session, byte** keyPackage, nuint* length);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DaveKeyRatchetSafeHandle daveSessionGetKeyRatchet(DaveSessionSafeHandle session, string userId);

    [LibraryImport(Lib)]
    internal static partial void daveKeyRatchetDestroy(IntPtr keyRatchet);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool daveCommitResultIsFailed(DaveCommitResultSafeHandle commitResult);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool daveCommitResultIsIgnored(DaveCommitResultSafeHandle commitResult);

    [LibraryImport(Lib)]
    internal static partial void daveCommitResultDestroy(IntPtr commitResult);

    [LibraryImport(Lib)]
    internal static partial void daveWelcomeResultDestroy(IntPtr welcomeResult);

    [LibraryImport(Lib)]
    internal static partial DaveEncryptorSafeHandle daveEncryptorCreate();

    [LibraryImport(Lib)]
    internal static partial void daveEncryptorDestroy(IntPtr encryptor);

    [LibraryImport(Lib)]
    internal static partial void daveEncryptorSetKeyRatchet(DaveEncryptorSafeHandle encryptor, DaveKeyRatchetSafeHandle keyRatchet);

    [LibraryImport(Lib, EntryPoint = "daveEncryptorSetKeyRatchet")]
    internal static partial void daveEncryptorClearKeyRatchet(DaveEncryptorSafeHandle encryptor, IntPtr keyRatchet);

    [LibraryImport(Lib)]
    internal static partial void daveEncryptorSetPassthroughMode(DaveEncryptorSafeHandle encryptor, [MarshalAs(UnmanagedType.U1)] bool passthroughMode);

    [LibraryImport(Lib)]
    internal static partial void daveEncryptorAssignSsrcToCodec(DaveEncryptorSafeHandle encryptor, uint ssrc, DaveCodec codecType);

    [LibraryImport(Lib)]
    internal static partial nuint daveEncryptorGetMaxCiphertextByteSize(DaveEncryptorSafeHandle encryptor, DaveMediaType mediaType, nuint frameSize);

    [LibraryImport(Lib)]
    internal static partial DaveEncryptorResult daveEncryptorEncrypt(
        DaveEncryptorSafeHandle encryptor, DaveMediaType mediaType, uint ssrc,
        byte* frame, nuint frameLength, byte* encryptedFrame, nuint encryptedFrameCapacity, nuint* bytesWritten);

    /// <summary><see cref="daveFree"/> as a delegate for <see cref="DaveMarshal.TakeBytes"/>.</summary>
    internal static readonly Action<IntPtr> Free = p => daveFree((void*)p);
}

internal sealed class DaveSessionSafeHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        DaveNative.daveSessionDestroy(handle);
        return true;
    }
}

internal sealed class DaveCommitResultSafeHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        DaveNative.daveCommitResultDestroy(handle);
        return true;
    }
}

internal sealed class DaveWelcomeResultSafeHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        DaveNative.daveWelcomeResultDestroy(handle);
        return true;
    }
}

internal sealed class DaveKeyRatchetSafeHandle() : SafeHandleZeroOrMinusOneIsInvalid(true), IDaveKeyRatchet
{
    protected override bool ReleaseHandle()
    {
        DaveNative.daveKeyRatchetDestroy(handle);
        return true;
    }
}

internal sealed class DaveEncryptorSafeHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        DaveNative.daveEncryptorDestroy(handle);
        return true;
    }
}

/// <summary>Production <see cref="ILibDave"/> over <c>libdave.so</c>.</summary>
internal sealed class LibDaveNative : ILibDave
{
    public static readonly LibDaveNative Instance = new();

    private LibDaveNative()
    {
    }

    public ushort MaxSupportedProtocolVersion => DaveNative.daveMaxSupportedProtocolVersion();

    public IDaveMlsSession CreateSession(Action<string, string> onMlsFailure) => new Session(onMlsFailure);

    public IDaveEncryptor CreateEncryptor() => new Encryptor();

    private sealed unsafe class Session : IDaveMlsSession
    {
        private readonly DaveSessionSafeHandle _handle;
        private GCHandle _callbackTarget;

        public Session(Action<string, string> onMlsFailure)
        {
            // The callback target stays pinned (as a GCHandle) for the native session's lifetime.
            _callbackTarget = GCHandle.Alloc(onMlsFailure);
            _handle = DaveNative.daveSessionCreate(null, null, &OnMlsFailure, (void*)GCHandle.ToIntPtr(_callbackTarget));
            if (_handle.IsInvalid)
            {
                _callbackTarget.Free();
                throw new InvalidOperationException("daveSessionCreate failed");
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnMlsFailure(byte* source, byte* reason, void* userData)
        {
            try
            {
                var target = (Action<string, string>)GCHandle.FromIntPtr((IntPtr)userData).Target!;
                target(
                    Marshal.PtrToStringUTF8((IntPtr)source) ?? string.Empty,
                    Marshal.PtrToStringUTF8((IntPtr)reason) ?? string.Empty);
            }
            catch
            {
                // Exceptions must never unwind into native code.
            }
        }

        public void Init(ushort version, ulong groupId, string selfUserId) =>
            DaveNative.daveSessionInit(_handle, version, groupId, selfUserId);

        public void Reset() => DaveNative.daveSessionReset(_handle);

        public byte[] GetMarshalledKeyPackage()
        {
            byte* data = null;
            nuint length = 0;
            DaveNative.daveSessionGetMarshalledKeyPackage(_handle, &data, &length);
            return DaveMarshal.TakeBytes((IntPtr)data, length, DaveNative.Free);
        }

        public void SetExternalSender(ReadOnlySpan<byte> externalSender)
        {
            fixed (byte* p = externalSender)
            {
                DaveNative.daveSessionSetExternalSender(_handle, p, (nuint)externalSender.Length);
            }
        }

        public byte[] ProcessProposals(ReadOnlySpan<byte> proposals, IReadOnlyList<string> recognizedUserIds)
        {
            using var users = new DaveMarshal.Utf8StringArray(recognizedUserIds);
            byte* data = null;
            nuint length = 0;
            fixed (byte* p = proposals)
            {
                DaveNative.daveSessionProcessProposals(
                    _handle, p, (nuint)proposals.Length, users.Pointer, users.Count, &data, &length);
            }

            return DaveMarshal.TakeBytes((IntPtr)data, length, DaveNative.Free);
        }

        public DaveCommitOutcome ProcessCommit(ReadOnlySpan<byte> commit)
        {
            DaveCommitResultSafeHandle result;
            fixed (byte* p = commit)
            {
                result = DaveNative.daveSessionProcessCommit(_handle, p, (nuint)commit.Length);
            }

            using (result)
            {
                if (result.IsInvalid || DaveNative.daveCommitResultIsFailed(result))
                {
                    return DaveCommitOutcome.Failed;
                }

                return DaveNative.daveCommitResultIsIgnored(result) ? DaveCommitOutcome.Ignored : DaveCommitOutcome.Success;
            }
        }

        public bool ProcessWelcome(ReadOnlySpan<byte> welcome, IReadOnlyList<string> recognizedUserIds)
        {
            using var users = new DaveMarshal.Utf8StringArray(recognizedUserIds);
            DaveWelcomeResultSafeHandle result;
            fixed (byte* p = welcome)
            {
                result = DaveNative.daveSessionProcessWelcome(_handle, p, (nuint)welcome.Length, users.Pointer, users.Count);
            }

            using (result)
            {
                return !result.IsInvalid;
            }
        }

        public IDaveKeyRatchet? GetKeyRatchet(string userId)
        {
            var ratchet = DaveNative.daveSessionGetKeyRatchet(_handle, userId);
            if (ratchet.IsInvalid)
            {
                ratchet.Dispose();
                return null;
            }

            return ratchet;
        }

        public void Dispose()
        {
            _handle.Dispose();
            if (_callbackTarget.IsAllocated)
            {
                _callbackTarget.Free();
            }
        }
    }

    private sealed unsafe class Encryptor : IDaveEncryptor
    {
        private readonly DaveEncryptorSafeHandle _handle;

        public Encryptor()
        {
            _handle = DaveNative.daveEncryptorCreate();
            if (_handle.IsInvalid)
            {
                throw new InvalidOperationException("daveEncryptorCreate failed");
            }
        }

        public void SetKeyRatchet(IDaveKeyRatchet? keyRatchet)
        {
            if (keyRatchet is null)
            {
                DaveNative.daveEncryptorClearKeyRatchet(_handle, IntPtr.Zero);
            }
            else
            {
                DaveNative.daveEncryptorSetKeyRatchet(_handle, (DaveKeyRatchetSafeHandle)keyRatchet);
            }
        }

        public void SetPassthroughMode(bool passthrough) => DaveNative.daveEncryptorSetPassthroughMode(_handle, passthrough);

        public void AssignSsrcToCodec(uint ssrc, DaveCodec codec) => DaveNative.daveEncryptorAssignSsrcToCodec(_handle, ssrc, codec);

        public int GetMaxCiphertextByteSize(DaveMediaType mediaType, int frameSize) =>
            checked((int)DaveNative.daveEncryptorGetMaxCiphertextByteSize(_handle, mediaType, (nuint)frameSize));

        public DaveEncryptorResult Encrypt(DaveMediaType mediaType, uint ssrc, ReadOnlySpan<byte> frame, Span<byte> output, out int written)
        {
            nuint bytesWritten = 0;
            DaveEncryptorResult result;
            fixed (byte* f = frame)
            fixed (byte* o = output)
            {
                result = DaveNative.daveEncryptorEncrypt(
                    _handle, mediaType, ssrc, f, (nuint)frame.Length, o, (nuint)output.Length, &bytesWritten);
            }

            written = checked((int)bytesWritten);
            return result;
        }

        public void Dispose() => _handle.Dispose();
    }
}

/// <summary>libdave's log severities (<c>DAVELoggingSeverity</c>).</summary>
public enum DaveLogSeverity
{
    Verbose = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Routes libdave's own logging (by default printed to stdout, including multi-kilobyte hex dumps of every MLS
/// message) into the bot's logger, so it can be filtered by level.
/// </summary>
public static unsafe class DaveLogging
{
    private static Action<DaveLogSeverity, string>? _sink;

    /// <summary>
    /// Install <paramref name="sink"/> as libdave's log sink. Returns false when <c>libdave.so</c> is not present
    /// (the sink then never fires).
    /// </summary>
    public static bool Install(Action<DaveLogSeverity, string> sink)
    {
        _sink = sink;
        try
        {
            DaveNative.daveSetLogSinkCallback(&OnLog); // the module initializer already registered the resolver
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(int severity, byte* file, int line, byte* message)
    {
        try
        {
            if (severity >= 4 || _sink is not { } sink)
            {
                return;
            }

            var source = Marshal.PtrToStringUTF8((IntPtr)file) ?? "?";
            var text = Marshal.PtrToStringUTF8((IntPtr)message) ?? string.Empty;
            sink((DaveLogSeverity)Math.Clamp(severity, 0, 3), $"({Path.GetFileName(source)}:{line}) {text}");
        }
        catch
        {
            // Exceptions must never unwind into native code.
        }
    }
}

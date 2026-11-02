using System.Buffers;
using System.Globalization;
using Slopsoil.Discord.Native;

namespace Slopsoil.Discord.Dave;

/// <summary>
/// <see cref="IDaveSession"/> backed by libdave (port of <c>davey_compat.DaveSession</c>): one MLS session
/// plus one frame encryptor, passthrough until our own key ratchet is installed by a commit or welcome.
/// Thread-safe: MLS operations (gateway) and encryption (media threads) are serialised.
/// </summary>
public sealed class LibDaveSession : IDaveSession
{
    private readonly Action<string>? _log;
    private readonly IDaveMlsSession _session;
    private readonly IDaveEncryptor _encryptor;
    private readonly Dictionary<uint, DaveCodec> _ssrcCodecs = [];
    private readonly Lock _lock = new();

    private IDaveKeyRatchet? _ratchet;
    private int _protocolVersion;
    private ulong _userId;
    private ulong _channelId;
    private bool _ready;
    private bool _disposed;

    /// <summary>Create a session over the system <c>libdave.so</c> and initialise it.</summary>
    /// <param name="log">Receives MLS failures and warnings (e.g. a missing key ratchet).</param>
    public LibDaveSession(int protocolVersion, ulong userId, ulong channelId, Action<string>? log = null)
        : this(LibDaveNative.Instance, protocolVersion, userId, channelId, log)
    {
    }

    internal LibDaveSession(ILibDave dave, int protocolVersion, ulong userId, ulong channelId, Action<string>? log = null)
    {
        ValidateVersion(protocolVersion);
        _log = log;
        _session = dave.CreateSession((source, reason) => _log?.Invoke($"[DAVE] MLS failure: {source} – {reason}"));
        try
        {
            _encryptor = dave.CreateEncryptor();
        }
        catch
        {
            _session.Dispose();
            throw;
        }

        _encryptor.SetPassthroughMode(true);
        Init(protocolVersion, userId, channelId);
    }

    /// <summary>Highest DAVE protocol version libdave supports (<c>daveMaxSupportedProtocolVersion</c>).</summary>
    public static int MaxSupportedProtocolVersion => LibDaveNative.Instance.MaxSupportedProtocolVersion;

    public int ProtocolVersion
    {
        get
        {
            lock (_lock)
            {
                return _protocolVersion;
            }
        }
    }

    public bool Ready
    {
        get
        {
            lock (_lock)
            {
                return _ready;
            }
        }
    }

    /// <summary>Frames sent unencrypted because libdave failed to encrypt them after the session was ready.</summary>
    public long EncryptFailures { get; private set; }

    public void Reinit(int protocolVersion, ulong userId, ulong channelId)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ValidateVersion(protocolVersion);
            ResetLocked();
            Init(protocolVersion, userId, channelId);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ResetLocked();
        }
    }

    public byte[] GetSerializedKeyPackage()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return _session.GetMarshalledKeyPackage();
        }
    }

    public void SetExternalSender(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _session.SetExternalSender(data);
        }
    }

    public byte[]? ProcessProposals(DaveProposalsOperation operation, ReadOnlySpan<byte> proposals, IReadOnlyCollection<ulong> recognizedUserIds)
    {
        // The gateway strips the operation-type byte (message[3]); libdave expects it in front of the proposals.
        var full = new byte[proposals.Length + 1];
        full[0] = operation == DaveProposalsOperation.Append ? (byte)0 : (byte)1;
        proposals.CopyTo(full.AsSpan(1));
        lock (_lock)
        {
            ThrowIfDisposed();
            var result = _session.ProcessProposals(full, RecognizedUsers(recognizedUserIds));
            return result.Length == 0 ? null : result;
        }
    }

    public bool ProcessCommit(ReadOnlySpan<byte> commit)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            switch (_session.ProcessCommit(commit))
            {
                case DaveCommitOutcome.Failed:
                    _log?.Invoke("[DAVE] commit rejected by libdave");
                    return false;
                case DaveCommitOutcome.Ignored:
                    return true;
                default:
                    RefreshKey();
                    return true;
            }
        }
    }

    public bool ProcessWelcome(ReadOnlySpan<byte> welcome, IReadOnlyCollection<ulong> recognizedUserIds)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_session.ProcessWelcome(welcome, RecognizedUsers(recognizedUserIds)))
            {
                _log?.Invoke("[DAVE] welcome rejected by libdave");
                return false;
            }

            RefreshKey();
            return true;
        }
    }

    public void SetPassthroughMode(bool passthrough)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _encryptor.SetPassthroughMode(passthrough);
        }
    }

    public byte[] EncryptOpus(uint ssrc, ReadOnlySpan<byte> frame) => Encrypt(DaveMediaType.Audio, DaveCodec.Opus, ssrc, frame);

    public void RegisterVideoSsrc(uint ssrc)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            EnsureCodec(ssrc, DaveCodec.H264);
        }
    }

    public byte[] EncryptH264(uint ssrc, ReadOnlySpan<byte> accessUnit) => Encrypt(DaveMediaType.Video, DaveCodec.H264, ssrc, accessUnit);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _encryptor.Dispose();
            _ratchet?.Dispose();
            _ratchet = null;
            _session.Dispose();
        }
    }

    private static void ValidateVersion(int protocolVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(protocolVersion);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(protocolVersion, ushort.MaxValue);
    }

    private void Init(int protocolVersion, ulong userId, ulong channelId)
    {
        _protocolVersion = protocolVersion;
        _userId = userId;
        _channelId = channelId;
        if (protocolVersion > 0)
        {
            // group id == voice channel id; libdave takes the user id as a decimal string.
            _session.Init((ushort)protocolVersion, _channelId, UserIdString(_userId));
        }
    }

    private void ResetLocked()
    {
        _session.Reset();
        _ready = false;
        _encryptor.SetKeyRatchet(null);
        _encryptor.SetPassthroughMode(true);
        _ratchet?.Dispose();
        _ratchet = null;
    }

    /// <summary>Pull our own key ratchet from the MLS session and hand it to the encryptor.</summary>
    private void RefreshKey()
    {
        var ratchet = _session.GetKeyRatchet(UserIdString(_userId));
        if (ratchet is null)
        {
            _log?.Invoke("[DAVE] WARNING: no key ratchet for self after commit/welcome");
            return;
        }

        _encryptor.SetKeyRatchet(ratchet);
        _encryptor.SetPassthroughMode(false);

        // The encryptor borrows the ratchet: release the previous one only once it has been replaced.
        _ratchet?.Dispose();
        _ratchet = ratchet;
        _ready = true;
    }

    /// <summary>Users libdave should trust: always ourselves, plus the current channel members.</summary>
    private string[] RecognizedUsers(IReadOnlyCollection<ulong> members)
    {
        var users = new SortedSet<ulong>(members) { _userId };
        return [.. users.Select(UserIdString)];
    }

    private byte[] Encrypt(DaveMediaType mediaType, DaveCodec codec, uint ssrc, ReadOnlySpan<byte> frame)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            EnsureCodec(ssrc, codec);
            if (!_ready)
            {
                return frame.ToArray(); // passthrough until a key ratchet is installed
            }

            var capacity = _encryptor.GetMaxCiphertextByteSize(mediaType, frame.Length);
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 1));
            try
            {
                var result = _encryptor.Encrypt(mediaType, ssrc, frame, buffer.AsSpan(0, capacity), out var written);
                if (result != DaveEncryptorResult.Success || written < 0 || written > capacity)
                {
                    EncryptFailures++;
                    return frame.ToArray();
                }

                return buffer.AsSpan(0, written).ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private void EnsureCodec(uint ssrc, DaveCodec codec)
    {
        if (_ssrcCodecs.TryGetValue(ssrc, out var current) && current == codec)
        {
            return;
        }

        _encryptor.AssignSsrcToCodec(ssrc, codec);
        _ssrcCodecs[ssrc] = codec;
    }

    private static string UserIdString(ulong id) => id.ToString(CultureInfo.InvariantCulture);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

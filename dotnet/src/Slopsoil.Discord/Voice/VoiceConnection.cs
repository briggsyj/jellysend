using System.Text.Json;
using System.Text.Json.Nodes;
using Slopsoil.Discord.Dave;
using Slopsoil.Discord.Gateway;

namespace Slopsoil.Discord.Voice;

/// <summary>Self-state flags sent with gateway op 4.</summary>
public readonly record struct VoiceStateFlags(bool SelfMute = false, bool SelfDeaf = true, bool SelfVideo = false);

/// <summary>
/// The main voice connection for a guild channel: gateway op 4, then VOICE_STATE_UPDATE (our session)
/// + VOICE_SERVER_UPDATE, then a <see cref="VoiceSession"/> to the voice server.
/// </summary>
public sealed class VoiceConnection : IAsyncDisposable
{
    private readonly IGatewayClient _gateway;
    private readonly VoiceOptions _options;
    private VoiceSession? _session;
    private int _disconnected;

    private VoiceConnection(IGatewayClient gateway, ulong guildId, ulong channelId, VoiceOptions options)
    {
        _gateway = gateway;
        GuildId = guildId;
        ChannelId = channelId;
        _options = options;
    }

    public ulong GuildId { get; }

    public ulong ChannelId { get; }

    /// <summary>Voice session id from our VOICE_STATE_UPDATE (go-live identifies with it too).</summary>
    public string SessionId { get; private set; } = "";

    public VoiceSession Session => _session ?? throw new InvalidOperationException("voice connection is not established");

    public uint Ssrc => Session.Ssrc;

    public uint VideoSsrc => Session.VideoSsrc;

    public string Mode => Session.Mode;

    public byte[] SecretKey => Session.SecretKey;

    public IDaveSession? DaveSession => Session.DaveSession;

    public bool CanEncrypt => Session.CanEncrypt;

    public bool SendPacket(ReadOnlySpan<byte> packet) => _session?.SendPacket(packet) ?? false;

    /// <summary>Join <paramref name="channelId"/> and complete the voice handshake within <see cref="VoiceOptions.ConnectTimeout"/>.</summary>
    /// <exception cref="TimeoutException">The dispatches or handshake did not arrive in time (we left the channel again).</exception>
    public static async Task<VoiceConnection> ConnectAsync(
        IGatewayClient gateway, ulong guildId, ulong channelId, VoiceOptions options,
        VoiceStateFlags flags = default, CancellationToken ct = default)
    {
        var connection = new VoiceConnection(gateway, guildId, channelId, options);
        using var timeout = new CancellationTokenSource(options.ConnectTimeout, options.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await connection.ConnectCoreAsync(flags, linked.Token).ConfigureAwait(false);
            return connection;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await connection.DisconnectAsync().ConfigureAwait(false);
            throw new TimeoutException($"voice connect to channel {channelId} timed out after {options.ConnectTimeout}");
        }
        catch
        {
            await connection.DisconnectAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ConnectCoreAsync(VoiceStateFlags flags, CancellationToken ct)
    {
        var state = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new TaskCompletionSource<(string Token, string Endpoint)>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnDispatch(string type, JsonElement d)
        {
            if (VoiceJson.Snowflake(d, "guild_id") != GuildId)
            {
                return;
            }

            if (type == "VOICE_STATE_UPDATE"
                && VoiceJson.Snowflake(d, "user_id") == _gateway.UserId
                && VoiceJson.Snowflake(d, "channel_id") is not null
                && VoiceJson.String(d, "session_id") is { } sessionId)
            {
                state.TrySetResult(sessionId);
            }
            else if (type == "VOICE_SERVER_UPDATE"
                && VoiceJson.String(d, "token") is { } token
                && VoiceJson.String(d, "endpoint") is { } endpoint)
            {
                // A null endpoint means "awaiting endpoint": keep waiting for the next update.
                server.TrySetResult((token, VoiceJson.StripScheme(endpoint)));
            }
        }

        _gateway.Dispatch += OnDispatch;
        try
        {
            await _gateway.SendAsync(GatewayOp.VoiceStateUpdate, VoiceStatePayload(ChannelId, flags), ct).ConfigureAwait(false);
            SessionId = await state.Task.WaitAsync(ct).ConfigureAwait(false);
            var (token, endpoint) = await server.Task.WaitAsync(ct).ConfigureAwait(false);
            var info = new VoiceServerInfo(endpoint, GuildId, _gateway.UserId, SessionId, token, ChannelId)
            {
                ChannelMembers = _options.ChannelMembers is { } members ? () => members(GuildId, ChannelId) : null,
            };
            _session = await VoiceSession.ConnectAsync(info, _options, ct).ConfigureAwait(false);
        }
        finally
        {
            _gateway.Dispatch -= OnDispatch;
        }
    }

    internal JsonObject VoiceStatePayload(ulong? channelId, VoiceStateFlags flags) => new()
    {
        ["guild_id"] = VoiceJson.Id(GuildId),
        ["channel_id"] = channelId is { } c ? VoiceJson.Id(c) : null,
        ["self_mute"] = flags.SelfMute,
        ["self_deaf"] = flags.SelfDeaf,
        ["self_video"] = flags.SelfVideo,
    };

    /// <summary>Leave the channel (op 4 with no channel) and close the voice session. Idempotent.</summary>
    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
        {
            return;
        }

        try
        {
            await _gateway.SendAsync(
                GatewayOp.VoiceStateUpdate, VoiceStatePayload(null, new VoiceStateFlags(SelfDeaf: false)), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"voice: could not send leave: {ex.Message}");
        }

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}

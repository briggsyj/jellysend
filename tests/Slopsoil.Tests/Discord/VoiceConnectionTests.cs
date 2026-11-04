using System.Buffers.Binary;
using System.Text.Json;
using Slopsoil.Discord.Dave;
using Slopsoil.Discord.Gateway;
using Slopsoil.Discord.Rtp;
using Slopsoil.Discord.Voice;

namespace Slopsoil.Tests.Discord;

public class VoiceConnectionTests
{
    private const ulong Guild = 10;
    private const ulong Channel = 20;
    private const ulong User = 42;

    private sealed record Rig(FakeGateway Gateway, FakeWebSocket Ws, FakeUdp Udp, FakeDave Dave, VoiceOptions Options);

    private static Rig MakeRig(
        FakeWebSocket? ws = null, FakeUdp? udp = null, bool answerGateway = true, bool withDave = true,
        TimeSpan? timeout = null, TimeProvider? time = null)
    {
        ws ??= VoiceServerScript.Create();
        udp ??= new FakeUdp();
        var dave = new FakeDave();
        var gateway = new FakeGateway { UserId = User };
        if (answerGateway)
        {
            gateway.OnSend = (g, op, d) =>
            {
                if (op != GatewayOp.VoiceStateUpdate || d.GetProperty("channel_id").ValueKind == JsonValueKind.Null)
                {
                    return;
                }

                // Noise first: another guild, another user, an "awaiting endpoint" server update.
                g.Raise("VOICE_STATE_UPDATE", """{"guild_id":"11","channel_id":"20","user_id":"42","session_id":"other"}""");
                g.Raise("VOICE_STATE_UPDATE", """{"guild_id":"10","channel_id":"20","user_id":"7","session_id":"other"}""");
                g.Raise("VOICE_SERVER_UPDATE", """{"guild_id":"10","token":"tok","endpoint":null}""");
                g.Raise("VOICE_STATE_UPDATE", """{"guild_id":"10","channel_id":"20","user_id":"42","session_id":"voice-sess"}""");
                g.Raise("VOICE_SERVER_UPDATE", """{"guild_id":10,"token":"tok","endpoint":"wss://eu.discord.media:443"}""");
            };
        }

        var options = new VoiceOptions
        {
            WebSockets = new FakeWebSocketFactory(ws),
            Udp = new FakeUdpFactory(udp),
            DaveSessionFactory = withDave ? () => dave : null,
            ConnectTimeout = timeout ?? Wait.Timeout,
            Time = time ?? TimeProvider.System,
        };
        return new Rig(gateway, ws, udp, dave, options);
    }

    private static Task<VoiceConnection> Connect(Rig rig, VoiceStateFlags flags = default) =>
        VoiceConnection.ConnectAsync(rig.Gateway, Guild, Channel, rig.Options, flags, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Connect_sends_voice_state_and_identifies_with_video()
    {
        var rig = MakeRig();
        await using var conn = await Connect(rig, new VoiceStateFlags(SelfMute: true, SelfDeaf: true));

        var (op, d) = rig.Gateway.Sent[0];
        Assert.Equal(GatewayOp.VoiceStateUpdate, op);
        Assert.Equal(
            """{"guild_id":"10","channel_id":"20","self_mute":true,"self_deaf":true,"self_video":false}""",
            d.GetRawText());
        Assert.Equal("voice-sess", conn.SessionId);
        Assert.Equal(new Uri("wss://eu.discord.media:443/?v=8"), rig.Ws.Uri);
        Assert.Equal(0, rig.Gateway.Subscribers);

        var identify = await rig.Ws.NextTextAsync(VoiceOp.Identify);
        Assert.Equal(
            """{"server_id":"10","user_id":"42","session_id":"voice-sess","token":"tok","max_dave_protocol_version":1,"video":true,"streams":[{"type":"video","rid":"100","quality":100}]}""",
            identify.GetRawText());
    }

    [Fact]
    public async Task Ready_does_ip_discovery_and_selects_protocol_with_codecs()
    {
        var rig = MakeRig();
        await using var conn = await Connect(rig);

        Assert.Equal(("198.51.100.1", 50001), rig.Udp.Connected);
        var request = rig.Udp.Sent[0];
        Assert.Equal(74, request.Length);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(request));
        Assert.Equal(70, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2)));
        Assert.Equal(VoiceServerScript.Ssrc, BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4)));
        Assert.All(request[8..], b => Assert.Equal(0, b));

        Assert.Equal("{}", (await rig.Ws.NextTextAsync(VoiceOp.VoiceBackendVersion)).GetRawText());
        var select = await rig.Ws.NextTextAsync(VoiceOp.SelectProtocol);
        Assert.Equal(
            """{"protocol":"udp","data":{"address":"203.0.113.7","port":50004,"mode":"aead_xchacha20_poly1305_rtpsize"},"codecs":[{"name":"opus","type":"audio","priority":1000,"payload_type":120},{"name":"H264","type":"video","priority":1000,"payload_type":101,"rtx_payload_type":102}]}""",
            select.GetRawText());
        Assert.Equal("203.0.113.7", conn.Session.ExternalIp);
        Assert.Equal(50004, conn.Session.ExternalPort);
    }

    [Fact]
    public async Task Session_description_sets_mode_key_and_sends_not_speaking()
    {
        var rig = MakeRig();
        await using var conn = await Connect(rig);

        Assert.Equal(VoiceServerScript.Ssrc, conn.Ssrc);
        Assert.Equal(VoiceServerScript.Ssrc + 1, conn.VideoSsrc);
        Assert.Equal(VoiceServerScript.Ssrc + 2, conn.Session.RtxSsrc);
        Assert.Equal(TransportCrypto.XChaChaRtpSize, conn.Mode);
        Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i), conn.SecretKey);
        Assert.Equal(0, conn.Session.DaveProtocolVersion);
        Assert.Null(conn.DaveSession);
        Assert.False(conn.CanEncrypt);
        Assert.Equal(3, conn.Session.SeqAck);
        var speaking = await rig.Ws.NextTextAsync(VoiceOp.Speaking);
        Assert.Equal("""{"speaking":0,"delay":0,"ssrc":1234}""", speaking.GetRawText());
    }

    [Theory]
    [InlineData("[\"xsalsa20_poly1305_lite\",\"xsalsa20_poly1305\"]", TransportCrypto.XSalsaLite)]
    [InlineData("[\"aead_aes256_gcm_rtpsize\",\"aead_xchacha20_poly1305_rtpsize\"]", TransportCrypto.XChaChaRtpSize)]
    public async Task Mode_preference(string offered, string expected)
    {
        var rig = MakeRig(VoiceServerScript.Create(modes: offered));
        await using var conn = await Connect(rig);
        Assert.Equal(expected, conn.Mode);
    }

    [Fact]
    public async Task No_supported_mode_fails_and_leaves_the_channel()
    {
        var rig = MakeRig(VoiceServerScript.Create(modes: "[\"aead_aes256_gcm_rtpsize\"]"));
        var ex = await Assert.ThrowsAsync<VoiceConnectionException>(() => Connect(rig));
        Assert.Contains("no supported encryption mode", ex.Message);
        Assert.Equal([GatewayOp.VoiceStateUpdate, GatewayOp.VoiceStateUpdate], rig.Gateway.Ops());
        Assert.Equal(JsonValueKind.Null, rig.Gateway.Sent[1].Data.GetProperty("channel_id").ValueKind);
        Assert.True(rig.Ws.Disposed);
        Assert.True(rig.Udp.Disposed);
    }

    [Fact]
    public async Task Heartbeat_carries_clock_and_seq_ack()
    {
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123));
        var rig = MakeRig(VoiceServerScript.Create(heartbeatMs: 20), time: clock);
        await using var conn = await Connect(rig);

        var beat = await NextHeartbeat(rig.Ws, 3);
        Assert.Equal(1_700_000_000_123, beat.GetProperty("t").GetInt64());

        // A binary frame's 2-byte sequence number also advances seq_ack.
        rig.Ws.PushBinary(77, VoiceOp.MlsExternalSender, 1);
        rig.Ws.Push(VoiceOp.HeartbeatAck, "{\"t\":1}", seq: 78);
        await WaitUntil(() => conn.Session.SeqAck == 78);
        rig.Ws.PushBinary(79, VoiceOp.MlsExternalSender, 1);
        await WaitUntil(() => conn.Session.SeqAck == 79);
        await NextHeartbeat(rig.Ws, 79);
    }

    private static async Task<JsonElement> NextHeartbeat(FakeWebSocket ws, int seqAck)
    {
        while (true)
        {
            var beat = await ws.NextTextAsync(VoiceOp.Heartbeat);
            if (beat.GetProperty("seq_ack").GetInt32() == seqAck)
            {
                return beat;
            }
        }
    }

    [Fact]
    public async Task Gateway_timeout_leaves_channel_and_unsubscribes()
    {
        var rig = MakeRig(answerGateway: false, timeout: TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() => Connect(rig));
        Assert.Equal([GatewayOp.VoiceStateUpdate, GatewayOp.VoiceStateUpdate], rig.Gateway.Ops());
        Assert.Equal(0, rig.Gateway.Subscribers);
        Assert.Null(rig.Ws.Uri);
    }

    [Fact]
    public async Task Handshake_timeout_closes_websocket_and_udp()
    {
        var rig = MakeRig(udp: new FakeUdp { AnswerDiscovery = false }, timeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => Connect(rig));
        Assert.Equal(1000, rig.Ws.ClosedWith);
        Assert.True(rig.Ws.Disposed);
        Assert.True(rig.Udp.Disposed);
        Assert.Equal(2, rig.Gateway.Ops().Count);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_timeout()
    {
        var rig = MakeRig(answerGateway: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VoiceConnection.ConnectAsync(rig.Gateway, Guild, Channel, rig.Options, default, cts.Token));
        Assert.Equal(2, rig.Gateway.Ops().Count);
    }

    [Fact]
    public async Task Server_close_during_handshake_fails_with_close_code()
    {
        var ws = VoiceServerScript.Create(describe: false);
        var inner = ws.OnText!;
        ws.OnText = (s, op, d) =>
        {
            inner(s, op, d);
            if (op == VoiceOp.SelectProtocol)
            {
                s.PushClose(4006);
            }
        };
        var rig = MakeRig(ws);
        var ex = await Assert.ThrowsAsync<VoiceConnectionException>(() => Connect(rig));
        Assert.Equal(4006, ex.CloseCode);
    }

    [Fact]
    public async Task Websocket_error_during_handshake_fails()
    {
        var ws = new FakeWebSocket();
        ws.OnText = (s, _, _) => s.Fail(new IOException("reset"));
        var rig = MakeRig(ws);
        var ex = await Assert.ThrowsAsync<VoiceConnectionException>(() => Connect(rig));
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Server_close_after_connect_completes_session()
    {
        var rig = MakeRig();
        await using var conn = await Connect(rig);
        rig.Ws.PushClose(4014);
        await conn.Session.Completion.WaitAsync(Wait.Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(4014, conn.Session.CloseCode);
    }

    [Fact]
    public async Task Disconnect_is_idempotent()
    {
        var rig = MakeRig(VoiceServerScript.Create(daveVersion: 1));
        var conn = await Connect(rig);
        rig.Ws.ThrowOnClose = true; // a failing close must not stop the cleanup

        await conn.DisconnectAsync();
        await conn.DisconnectAsync();
        await conn.DisposeAsync();

        Assert.Equal([GatewayOp.VoiceStateUpdate, GatewayOp.VoiceStateUpdate], rig.Gateway.Ops());
        Assert.Equal(
            """{"guild_id":"10","channel_id":null,"self_mute":false,"self_deaf":false,"self_video":false}""",
            rig.Gateway.Sent[1].Data.GetRawText());
        Assert.Equal(1000, rig.Ws.ClosedWith);
        Assert.True(rig.Ws.Disposed);
        Assert.True(rig.Udp.Disposed);
        Assert.True(rig.Dave.Disposed);
        Assert.False(conn.SendPacket([1, 2, 3]));
    }

    [Fact]
    public async Task Disconnect_tolerates_gateway_failure()
    {
        var rig = MakeRig();
        var conn = await Connect(rig);
        rig.Gateway.FailSend = new InvalidOperationException("gateway down");
        await conn.DisconnectAsync();
        Assert.True(rig.Ws.Disposed);
    }

    [Fact]
    public async Task SendPacket_goes_to_udp()
    {
        var rig = MakeRig();
        await using var conn = await Connect(rig);

        Assert.True(conn.SendPacket([0x80, 0x78, 1]));
        Assert.Equal([0x80, 0x78, 1], rig.Udp.Sent[^1]);

        rig.Udp.SendResult = false;
        Assert.False(conn.SendPacket([1]));

        rig.Udp.ThrowOnSend = new ObjectDisposedException("udp");
        Assert.False(conn.SendPacket([1]));
    }

    [Fact]
    public void Not_connected_surface_throws()
    {
        Assert.Throws<InvalidOperationException>(() => GetUnconnected().Session);

        static VoiceConnection GetUnconnected() =>
            (VoiceConnection)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VoiceConnection));
    }

    [Fact]
    public async Task Announce_video_sends_op_12_with_streams()
    {
        var rig = MakeRig();
        await using var conn = await Connect(rig);
        await conn.Session.AnnounceVideoAsync(TestContext.Current.CancellationToken);
        var video = await rig.Ws.NextTextAsync(VoiceOp.Video);
        Assert.Equal(
            """{"audio_ssrc":1234,"video_ssrc":1235,"rtx_ssrc":1236,"streams":[{"type":"video","rid":"100","ssrc":1235,"active":true,"quality":100,"rtx_ssrc":1236,"max_bitrate":10000000,"max_framerate":60,"max_resolution":{"type":"fixed","width":1920,"height":1080}}]}""",
            video.GetRawText());

        await conn.Session.SetSpeakingAsync(2, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await rig.Ws.NextTextAsync(VoiceOp.Speaking)).GetProperty("speaking").GetInt32());
    }

    [Fact]
    public async Task Clients_connect_and_disconnect_are_tracked()
    {
        var rig = MakeRig();
        await using var conn = await Connect(rig);
        rig.Ws.Push(VoiceOp.ClientsConnect, """{"user_ids":["5","6",7,"x"]}""");
        await WaitUntil(() => conn.Session.ConnectedUserIds.Count == 3);
        rig.Ws.Push(VoiceOp.ClientDisconnect, """{"user_id":"6"}""");
        rig.Ws.Push(VoiceOp.ClientsConnect, """{"other":1}""");
        rig.Ws.Push(VoiceOp.ClientDisconnect, """{"user_id":null}""");
        rig.Ws.Push(VoiceOp.ClientsConnect, """{"user_ids":["8"]}""");
        await WaitUntil(() => conn.Session.ConnectedUserIds.Contains(8UL));
        Assert.Equal([5UL, 7UL, 8UL], conn.Session.ConnectedUserIds.Order());
    }

    [Fact]
    public async Task Identify_advertises_dave_zero_without_factory()
    {
        var rig = MakeRig(withDave: false);
        await using var conn = await Connect(rig);
        var identify = await rig.Ws.NextTextAsync(VoiceOp.Identify);
        Assert.Equal(0, identify.GetProperty("max_dave_protocol_version").GetInt32());
    }

    [Fact]
    public async Task Dave_required_without_factory_fails()
    {
        var rig = MakeRig(VoiceServerScript.Create(daveVersion: 1), withDave: false);
        var ex = await Assert.ThrowsAsync<VoiceConnectionException>(() => Connect(rig));
        Assert.Contains("DAVE", ex.Message);
    }

    [Fact]
    public void Discovery_parsing()
    {
        Assert.True(VoiceSession.TryParseDiscoveryResponse(FakeUdp.DiscoveryReply("10.0.0.1", 65535, 1), out var ip, out var port));
        Assert.Equal(("10.0.0.1", 65535), (ip, port));
        Assert.False(VoiceSession.TryParseDiscoveryResponse(new byte[73], out _, out _));
        var noNul = FakeUdp.DiscoveryReply("1", 1, 1);
        noNul.AsSpan(8, 64).Fill((byte)'1');
        Assert.False(VoiceSession.TryParseDiscoveryResponse(noNul, out _, out _));
        Assert.Null(VoiceSession.ChooseMode(["xsalsa20_poly1305_suffix"]));
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait.Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met");
            }

            await Task.Delay(5);
        }
    }
}

/// <summary>DAVE opcodes 21–31 against a fake <see cref="IDaveSession"/>.</summary>
public class VoiceDaveTests
{
    private const ulong User = 42;
    private const ulong DaveChannel = 20;

    private sealed record Rig(VoiceSession Session, FakeWebSocket Ws, FakeDave Dave) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private static async Task<Rig> Connect(int daveVersion = 1, Func<IEnumerable<ulong>>? members = null)
    {
        var ws = VoiceServerScript.Create(daveVersion: daveVersion);
        var dave = new FakeDave();
        var options = new VoiceOptions
        {
            WebSockets = new FakeWebSocketFactory(ws),
            Udp = new FakeUdpFactory(new FakeUdp()),
            DaveSessionFactory = () => dave,
            MaxDaveProtocolVersion = 1,
        };
        var info = new VoiceServerInfo("voice.example", 10, User, "sess", "tok", DaveChannel) { ChannelMembers = members };
        var session = await VoiceSession.ConnectAsync(info, options, TestContext.Current.CancellationToken);
        return new Rig(session, ws, dave);
    }

    /// <summary>Push an external-sender frame and wait for it: everything pushed before has been handled.</summary>
    private static async Task Sync(Rig rig)
    {
        var count = rig.Dave.Calls.Count(c => c == "SetExternalSender");
        rig.Ws.PushBinary(0, VoiceOp.MlsExternalSender, 0xEE);
        await VoiceConnectionTests.WaitUntil(() => rig.Dave.Calls.Count(c => c == "SetExternalSender") > count);
    }

    private static List<(int Op, string D)> SentTexts(Rig rig) =>
        [.. rig.Ws.Drain().Where(f => f.IsText).Select(f =>
        {
            var root = JsonDocument.Parse(f.Text).RootElement;
            return (root.GetProperty("op").GetInt32(), root.GetProperty("d").GetRawText());
        })];

    [Fact]
    public async Task Session_description_with_dave_reinits_and_sends_key_package()
    {
        await using var rig = await Connect();
        Assert.Same(rig.Dave, rig.Session.DaveSession);
        Assert.Equal(["Reinit(1,42,20)", "GetSerializedKeyPackage"], rig.Dave.Calls);
        Assert.Equal([VoiceOp.MlsKeyPackage, 0xAA, 0xBB], await rig.Ws.NextBinaryAsync());
        Assert.Equal(1, rig.Session.DaveProtocolVersion);
        Assert.False(rig.Session.CanEncrypt);
        rig.Dave.Ready = true;
        Assert.True(rig.Session.CanEncrypt);
    }

    [Fact]
    public async Task External_sender_is_set()
    {
        await using var rig = await Connect();
        rig.Ws.PushBinary(5, VoiceOp.MlsExternalSender, 1, 2, 3);
        await rig.Dave.WaitForCallAsync("SetExternalSender");
        Assert.Equal([1, 2, 3], rig.Dave.LastData);
    }

    [Fact]
    public async Task Recognised_users_include_voice_channel_members_not_yet_announced()
    {
        await using var rig = await Connect(members: () => [42UL, 8UL, 9UL]);
        rig.Ws.Push(VoiceOp.ClientsConnect, """{"user_ids":["7"]}""");
        await VoiceConnectionTests.WaitUntil(() => rig.Session.ConnectedUserIds.Count == 1);
        rig.Ws.Drain();
        rig.Ws.PushBinary(9, VoiceOp.MlsProposals, 0, 0x50);
        await rig.Ws.NextBinaryAsync();
        Assert.Equal([7UL, 8UL, 9UL, 42UL], rig.Dave.LastRecognized.Order());
        Assert.Equal(1, rig.Dave.LastRecognized.Count(u => u == 42)); // self once
    }

    [Theory]
    [InlineData(0, DaveProposalsOperation.Append)]
    [InlineData(1, DaveProposalsOperation.Revoke)]
    public async Task Proposals_reply_with_commit_welcome(byte optype, DaveProposalsOperation expected)
    {
        await using var rig = await Connect();
        rig.Ws.Push(VoiceOp.ClientsConnect, """{"user_ids":["7","42"]}""");
        await VoiceConnectionTests.WaitUntil(() => rig.Session.ConnectedUserIds.Count == 2);
        rig.Ws.Drain();

        rig.Ws.PushBinary(9, VoiceOp.MlsProposals, optype, 0x50, 0x51);
        Assert.Equal([VoiceOp.MlsCommitWelcome, 0xC0, 0x11], await rig.Ws.NextBinaryAsync());
        Assert.Equal(expected, rig.Dave.LastOperation);
        Assert.Equal([0x50, 0x51], rig.Dave.LastData);
        Assert.Equal([7UL, 42UL], rig.Dave.LastRecognized.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proposals_without_result_or_failing_send_nothing(bool throws)
    {
        await using var rig = await Connect();
        rig.Dave.CommitWelcome = null;
        rig.Dave.Throw = throws;
        rig.Ws.Drain();
        rig.Ws.PushBinary(9, VoiceOp.MlsProposals, 0, 0x50);
        await rig.Dave.WaitForCallAsync("ProcessProposals");
        await Sync(rig);
        Assert.DoesNotContain(rig.Ws.Drain(), f => !f.IsText);
    }

    [Theory]
    [InlineData(VoiceOp.MlsAnnounceCommitTransition, "ProcessCommit")]
    [InlineData(VoiceOp.MlsWelcome, "ProcessWelcome")]
    public async Task Commit_or_welcome_success_sends_transition_ready(int op, string call)
    {
        await using var rig = await Connect();
        rig.Ws.Drain();
        rig.Ws.PushBinary(11, op, 0x00, 0x07, 0xDE, 0xAD);
        var ready = await rig.Ws.NextTextAsync(VoiceOp.DaveTransitionReady);
        Assert.Equal("""{"transition_id":7}""", ready.GetRawText());
        Assert.Contains(call, rig.Dave.Calls);
        Assert.Equal([0xDE, 0xAD], rig.Dave.LastData);
        if (op == VoiceOp.MlsWelcome)
        {
            Assert.Equal([User], rig.Dave.LastRecognized);
        }

        // Executing transition 7 keeps protocol version 1 (it was recorded as pending).
        rig.Ws.Push(VoiceOp.DaveExecuteTransition, """{"transition_id":7}""");
        await Sync(rig);
        Assert.Equal(1, rig.Session.DaveProtocolVersion);
    }

    [Fact]
    public async Task Commit_with_transition_zero_sends_nothing()
    {
        await using var rig = await Connect();
        rig.Ws.Drain();
        rig.Ws.PushBinary(11, VoiceOp.MlsAnnounceCommitTransition, 0x00, 0x00, 0x01);
        await rig.Dave.WaitForCallAsync("ProcessCommit");
        await Sync(rig);
        Assert.Empty(SentTexts(rig));
    }

    [Theory]
    [InlineData(VoiceOp.MlsAnnounceCommitTransition, false)]
    [InlineData(VoiceOp.MlsAnnounceCommitTransition, true)]
    [InlineData(VoiceOp.MlsWelcome, false)]
    public async Task Invalid_commit_or_welcome_recovers(int op, bool throws)
    {
        await using var rig = await Connect();
        rig.Dave.CommitOk = false;
        rig.Dave.WelcomeOk = false;
        rig.Dave.Throw = throws;
        rig.Ws.Drain();
        rig.Dave.Calls.Clear();

        rig.Ws.PushBinary(11, op, 0x01, 0x02, 0xFF);
        var invalid = await rig.Ws.NextTextAsync(VoiceOp.MlsInvalidCommitWelcome);
        Assert.Equal("""{"transition_id":258}""", invalid.GetRawText());
        Assert.Equal([VoiceOp.MlsKeyPackage, 0xAA, 0xBB], await rig.Ws.NextBinaryAsync());
        Assert.Contains("Reinit(1,42,20)", rig.Dave.Calls);
    }

    [Fact]
    public async Task Prepare_transition_zero_executes_immediately()
    {
        await using var rig = await Connect();
        rig.Ws.Drain();
        rig.Ws.Push(VoiceOp.DavePrepareTransition, """{"transition_id":0,"protocol_version":0}""");
        await Sync(rig);
        Assert.Equal(0, rig.Session.DaveProtocolVersion);
        Assert.Empty(SentTexts(rig));
    }

    [Fact]
    public async Task Downgrade_then_upgrade_transitions()
    {
        await using var rig = await Connect();
        rig.Ws.Drain();
        rig.Dave.Calls.Clear();

        // Downgrade to 0: passthrough + ready, executed later.
        rig.Ws.Push(VoiceOp.DavePrepareTransition, """{"transition_id":5,"protocol_version":0}""");
        Assert.Equal("""{"transition_id":5}""", (await rig.Ws.NextTextAsync(VoiceOp.DaveTransitionReady)).GetRawText());
        Assert.Equal(["SetPassthroughMode(True)"], rig.Dave.Calls);
        Assert.Equal(1, rig.Session.DaveProtocolVersion);
        rig.Ws.Push(VoiceOp.DaveExecuteTransition, """{"transition_id":5}""");
        await Sync(rig);
        Assert.Equal(0, rig.Session.DaveProtocolVersion);

        // Upgrade back: prepare (no passthrough for v1) then execute → encrypt again (passthrough off).
        rig.Dave.Calls.Clear();
        rig.Ws.Push(VoiceOp.DavePrepareTransition, """{"transition_id":6,"protocol_version":1}""");
        Assert.Equal("""{"transition_id":6}""", (await rig.Ws.NextTextAsync(VoiceOp.DaveTransitionReady)).GetRawText());
        rig.Ws.Push(VoiceOp.DaveExecuteTransition, """{"transition_id":6}""");
        await Sync(rig);
        Assert.Equal(1, rig.Session.DaveProtocolVersion);
        Assert.Equal(["SetPassthroughMode(False)", "SetExternalSender"], rig.Dave.Calls);
    }

    [Fact]
    public async Task Ignored_commit_sends_neither_ready_nor_invalid()
    {
        await using var rig = await Connect();
        rig.Dave.CommitOk = null;
        rig.Ws.Drain();
        rig.Ws.PushBinary(11, VoiceOp.MlsAnnounceCommitTransition, 0x00, 0x07, 0x01);
        await rig.Dave.WaitForCallAsync("ProcessCommit");
        await Sync(rig);
        Assert.Empty(SentTexts(rig));
        Assert.DoesNotContain(rig.Ws.Drain(), f => !f.IsText);
    }

    [Fact]
    public async Task Execute_unknown_transition_is_ignored()
    {
        await using var rig = await Connect();
        rig.Ws.Push(VoiceOp.DaveExecuteTransition, """{"transition_id":99}""");
        await Sync(rig);
        Assert.Equal(1, rig.Session.DaveProtocolVersion);
    }

    [Fact]
    public async Task Prepare_epoch_one_reinits_others_do_not()
    {
        await using var rig = await Connect();
        rig.Ws.Drain();
        rig.Dave.Calls.Clear();

        rig.Ws.Push(VoiceOp.DavePrepareEpoch, """{"epoch":2,"protocol_version":1}""");
        await Sync(rig);
        Assert.Equal(["SetExternalSender"], rig.Dave.Calls);

        rig.Dave.Calls.Clear();
        rig.Ws.Push(VoiceOp.DavePrepareEpoch, """{"epoch":1,"protocol_version":1}""");
        Assert.Equal([VoiceOp.MlsKeyPackage, 0xAA, 0xBB], await rig.Ws.NextBinaryAsync());
        Assert.Equal(["Reinit(1,42,20)", "GetSerializedKeyPackage"], rig.Dave.Calls);
    }

    [Fact]
    public async Task Prepare_epoch_with_version_zero_resets_to_passthrough()
    {
        await using var rig = await Connect();
        rig.Dave.Calls.Clear();
        rig.Ws.Push(VoiceOp.DavePrepareEpoch, """{"epoch":1,"protocol_version":0}""");
        await Sync(rig);
        Assert.Equal(["Reset", "SetPassthroughMode(True)", "SetExternalSender"], rig.Dave.Calls);
        Assert.Equal(0, rig.Session.DaveProtocolVersion);
    }

    [Fact]
    public async Task Dave_frames_without_a_session_are_ignored()
    {
        await using var rig = await Connect(daveVersion: 0);
        Assert.Null(rig.Session.DaveSession);
        rig.Ws.Drain();
        rig.Ws.PushBinary(300, VoiceOp.MlsProposals, 0, 1);
        rig.Ws.PushBytes(1, 2); // too short: ignored
        rig.Ws.Push(VoiceOp.DavePrepareTransition, """{"transition_id":5,"protocol_version":0}""");
        rig.Ws.Push(VoiceOp.ClientsConnect, """{"user_ids":["9"]}""");
        await VoiceConnectionTests.WaitUntil(() => rig.Session.ConnectedUserIds.Count == 1);
        Assert.Equal(300, rig.Session.SeqAck);
        Assert.Empty(rig.Ws.Drain());
        Assert.Empty(rig.Dave.Calls);
    }

    [Fact]
    public async Task Short_mls_frames_are_ignored()
    {
        await using var rig = await Connect();
        rig.Ws.Drain();
        rig.Dave.Calls.Clear();
        rig.Ws.PushBinary(1, VoiceOp.MlsProposals);
        rig.Ws.PushBinary(1, VoiceOp.MlsWelcome, 0);
        rig.Ws.PushBinary(1, 99, 0);
        await Sync(rig);
        Assert.Equal(["SetExternalSender"], rig.Dave.Calls);
        Assert.Empty(SentTexts(rig));
    }
}

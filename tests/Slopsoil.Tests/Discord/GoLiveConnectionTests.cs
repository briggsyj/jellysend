using System.Text.Json;
using Slopsoil.Discord.Gateway;
using Slopsoil.Discord.GoLive;
using Slopsoil.Discord.Voice;

namespace Slopsoil.Tests.Discord;

public class GoLiveConnectionTests
{
    private const ulong Guild = 10;
    private const ulong Channel = 20;
    private const string Key = "guild:10:20:42";

    private sealed record Rig(
        FakeGateway Gateway, FakeWebSocket Ws, FakeUdp Udp, FakeDave Dave, FakeWebSocketFactory Sockets, VoiceOptions Options)
    {
        /// <summary>Gateway op plus whether the stream websocket had announced video when it was sent.</summary>
        public List<(int Op, bool VideoAnnounced)> Timeline { get; } = [];
    }

    private static Rig MakeRig(
        FakeWebSocket? ws = null, bool sendCreate = true, bool sendServer = true, int daveVersion = 0,
        TimeSpan? timeout = null)
    {
        ws ??= VoiceServerScript.Create(daveVersion: daveVersion);
        var udp = new FakeUdp();
        var dave = new FakeDave();
        var sockets = new FakeWebSocketFactory(ws);
        var gateway = new FakeGateway { UserId = 42 };
        var videoAnnounced = false;
        var inner = ws.OnText;
        ws.OnText = (s, op, d) =>
        {
            inner?.Invoke(s, op, d);
            videoAnnounced |= op == VoiceOp.Video;
        };
        var options = new VoiceOptions
        {
            WebSockets = sockets,
            Udp = new FakeUdpFactory(udp),
            DaveSessionFactory = () => dave,
            ConnectTimeout = timeout ?? Wait.Timeout,
        };
        var rig = new Rig(gateway, ws, udp, dave, sockets, options);
        gateway.OnSend = (g, op, _) =>
        {
            rig.Timeline.Add((op, videoAnnounced));
            if (op != GatewayOp.StreamCreate)
            {
                return;
            }

            // Another user's stream must be ignored.
            g.Raise("STREAM_CREATE", """{"stream_key":"guild:10:20:7","rtc_server_id":"1"}""");
            g.Raise("STREAM_SERVER_UPDATE", """{"stream_key":"guild:10:20:7","endpoint":"wrong","token":"x"}""");
            if (sendCreate)
            {
                g.Raise("STREAM_CREATE", $$"""{"stream_key":"{{Key}}","rtc_server_id":"900","viewer_ids":[],"paused":false}""");
            }

            if (sendServer)
            {
                g.Raise("STREAM_SERVER_UPDATE", $$"""{"stream_key":"{{Key}}","endpoint":null,"token":"stok"}""");
                g.Raise("STREAM_SERVER_UPDATE", $$"""{"stream_key":"{{Key}}","endpoint":"wss://stream.discord.media","token":"stok"}""");
            }
        };
        return rig;
    }

    private static Task<GoLiveConnection> Connect(Rig rig) =>
        GoLiveConnection.ConnectAsync(rig.Gateway, Guild, Channel, "voice-sess", rig.Options, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Op_sequence_create_and_unpause_then_stream_socket()
    {
        var rig = MakeRig();
        await using var conn = await Connect(rig);

        // 18 then 22 straight away (golive.py order), both before the stream socket exists.
        Assert.Equal([(GatewayOp.StreamCreate, false), (GatewayOp.StreamSetPaused, false)], rig.Timeline);
        Assert.Equal(
            """{"type":"guild","guild_id":"10","channel_id":"20","preferred_region":null}""",
            rig.Gateway.Sent[0].Data.GetRawText());
        Assert.Equal($$"""{"stream_key":"{{Key}}","paused":false}""", rig.Gateway.Sent[1].Data.GetRawText());
        Assert.Equal(0, rig.Gateway.Subscribers);

        Assert.Equal(900UL, conn.ServerId);
        Assert.Equal(Key, conn.StreamKey);
        Assert.Equal(new Uri("wss://stream.discord.media/?v=8"), rig.Ws.Uri);
        var identify = await rig.Ws.NextTextAsync(VoiceOp.Identify);
        Assert.Equal("900", identify.GetProperty("server_id").GetString());
        Assert.Equal("42", identify.GetProperty("user_id").GetString());
        Assert.Equal("voice-sess", identify.GetProperty("session_id").GetString());
        Assert.Equal("stok", identify.GetProperty("token").GetString());
        Assert.True(identify.GetProperty("video").GetBoolean());
        var codecs = (await rig.Ws.NextTextAsync(VoiceOp.SelectProtocol)).GetProperty("codecs");
        Assert.Equal(101, codecs[1].GetProperty("payload_type").GetInt32());
        var video = await rig.Ws.NextTextAsync(VoiceOp.Video);
        Assert.Equal(VoiceServerScript.Ssrc + 1, video.GetProperty("video_ssrc").GetUInt32());

        Assert.Equal(VoiceServerScript.Ssrc, conn.Ssrc);
        Assert.Equal(VoiceServerScript.Ssrc + 1, conn.VideoSsrc);
        Assert.Equal("aead_xchacha20_poly1305_rtpsize", conn.Mode);
        Assert.Equal(32, conn.SecretKey.Length);
        Assert.Null(conn.DaveSession);
        Assert.False(conn.CanEncrypt);
        Assert.True(conn.SendPacket([9, 9]));
        Assert.Equal([9, 9], rig.Udp.Sent[^1]);
    }

    [Fact]
    public async Task Dave_group_is_server_id_minus_one()
    {
        var rig = MakeRig(daveVersion: 1);
        await using var conn = await Connect(rig);
        Assert.Same(rig.Dave, conn.DaveSession);
        Assert.Equal("Reinit(1,42,899)", rig.Dave.Calls[0]);
        Assert.Equal([VoiceOp.MlsKeyPackage, 0xAA, 0xBB], await rig.Ws.NextBinaryAsync());
    }

    [Fact]
    public async Task Disconnect_sends_single_stream_delete()
    {
        var rig = MakeRig(daveVersion: 1);
        var conn = await Connect(rig);

        await conn.DisconnectAsync();
        await conn.DisconnectAsync();
        await conn.DisposeAsync();

        Assert.Equal([GatewayOp.StreamCreate, GatewayOp.StreamSetPaused, GatewayOp.StreamDelete], rig.Gateway.Ops());
        Assert.Equal($$"""{"stream_key":"{{Key}}"}""", rig.Gateway.Sent[2].Data.GetRawText());
        Assert.Null(conn.StreamKey);
        Assert.Equal(1000, rig.Ws.ClosedWith);
        Assert.True(rig.Ws.Disposed);
        Assert.True(rig.Udp.Disposed);
        Assert.True(rig.Dave.Disposed);
        Assert.False(conn.SendPacket([1]));
    }

    [Fact]
    public async Task Concurrent_disconnects_send_one_delete()
    {
        var rig = MakeRig();
        var conn = await Connect(rig);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(conn.DisconnectAsync)));
        Assert.Single(rig.Gateway.Ops(), op => op == GatewayOp.StreamDelete);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Connect_timeout_cleans_up(bool sendCreate, bool sendServer)
    {
        var rig = MakeRig(sendCreate: sendCreate, sendServer: sendServer, timeout: TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() => Connect(rig));
        Assert.Equal([GatewayOp.StreamCreate, GatewayOp.StreamSetPaused, GatewayOp.StreamDelete], rig.Gateway.Ops());
        Assert.Equal($$"""{"stream_key":"{{Key}}"}""", rig.Gateway.Sent[2].Data.GetRawText());
        Assert.Equal(0, rig.Gateway.Subscribers);
        Assert.Empty(rig.Sockets.Created);
    }

    [Fact]
    public async Task Handshake_timeout_closes_stream_socket()
    {
        var rig = MakeRig(ws: VoiceServerScript.Create(describe: false), timeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => Connect(rig));
        Assert.Equal([GatewayOp.StreamCreate, GatewayOp.StreamSetPaused, GatewayOp.StreamDelete], rig.Gateway.Ops());
        Assert.True(rig.Ws.Disposed);
        Assert.True(rig.Udp.Disposed);
    }

    [Fact]
    public async Task Handshake_failure_cleans_up_and_propagates()
    {
        var ws = VoiceServerScript.Create(describe: false);
        var inner = ws.OnText!;
        ws.OnText = (s, op, d) =>
        {
            inner(s, op, d);
            if (op == VoiceOp.SelectProtocol)
            {
                s.PushClose(4004);
            }
        };
        var rig = MakeRig(ws);
        var ex = await Assert.ThrowsAsync<VoiceConnectionException>(() => Connect(rig));
        Assert.Equal(4004, ex.CloseCode);
        Assert.Equal([GatewayOp.StreamCreate, GatewayOp.StreamSetPaused, GatewayOp.StreamDelete], rig.Gateway.Ops());
        Assert.True(rig.Ws.Disposed);
    }

    [Fact]
    public async Task Stream_delete_failure_is_tolerated()
    {
        var rig = MakeRig();
        var conn = await Connect(rig);
        rig.Gateway.FailSend = new InvalidOperationException("gateway down");
        await conn.DisconnectAsync();
        Assert.Null(conn.StreamKey);
        Assert.True(rig.Ws.Disposed);
    }

    [Fact]
    public async Task Connects_from_voice_connection()
    {
        var voiceWs = VoiceServerScript.Create();
        var streamWs = VoiceServerScript.Create();
        var gateway = new FakeGateway { UserId = 42 };
        gateway.OnSend = (g, op, d) =>
        {
            if (op == GatewayOp.VoiceStateUpdate && d.GetProperty("channel_id").ValueKind != JsonValueKind.Null)
            {
                g.Raise("VOICE_STATE_UPDATE", """{"guild_id":"10","channel_id":"20","user_id":"42","session_id":"vs"}""");
                g.Raise("VOICE_SERVER_UPDATE", """{"guild_id":"10","token":"t","endpoint":"voice.example"}""");
            }
            else if (op == GatewayOp.StreamCreate)
            {
                g.Raise("STREAM_CREATE", $$"""{"stream_key":"{{Key}}","rtc_server_id":"900"}""");
                g.Raise("STREAM_SERVER_UPDATE", $$"""{"stream_key":"{{Key}}","endpoint":"stream.example","token":"st"}""");
            }
        };
        var options = new VoiceOptions
        {
            WebSockets = new FakeWebSocketFactory(voiceWs, streamWs),
            Udp = new FakeUdpFactory(new FakeUdp(), new FakeUdp()),
        };
        var ct = TestContext.Current.CancellationToken;
        await using var voice = await VoiceConnection.ConnectAsync(gateway, Guild, Channel, options, default, ct);
        await using var live = await GoLiveConnection.ConnectAsync(gateway, voice, options, ct);

        Assert.Equal(new Uri("wss://stream.example/?v=8"), streamWs.Uri);
        Assert.Equal("vs", (await streamWs.NextTextAsync(VoiceOp.Identify)).GetProperty("session_id").GetString());
        Assert.Equal(Guild, live.GuildId);
        Assert.Equal(Channel, live.ChannelId);
    }

    [Fact]
    public void Stream_key_format() => Assert.Equal("guild:1:2:3", GoLiveConnection.MakeStreamKey(1, 2, 3));

    [Fact]
    public void Unconnected_session_throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            ((GoLiveConnection)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GoLiveConnection))).Session);
}

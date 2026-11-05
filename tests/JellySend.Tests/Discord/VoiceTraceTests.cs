using System.Text.Json;
using JellySend.Discord.Gateway;
using JellySend.Discord.Voice;

namespace JellySend.Tests.Discord;

/// <summary>Spec 012: diagnostic lines for voice / screenshare events, never carrying secrets.</summary>
public sealed class VoiceTraceTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData(VoiceOp.Ready, "READY")]
    [InlineData(VoiceOp.SessionDescription, "SESSION_DESCRIPTION")]
    [InlineData(VoiceOp.MlsWelcome, "MLS_WELCOME")]
    [InlineData(VoiceOp.DavePrepareEpoch, "DAVE_PREPARE_EPOCH")]
    [InlineData(99, "?")]
    public void Op_names(int op, string name) => Assert.Equal(name, VoiceOp.Name(op));

    [Fact]
    public void Every_defined_op_has_a_name()
    {
        foreach (var field in typeof(VoiceOp).GetFields().Where(f => f.IsLiteral))
        {
            Assert.NotEqual("?", VoiceOp.Name((int)field.GetValue(null)!));
        }
    }

    [Theory]
    [InlineData(4022, "call terminated")]
    [InlineData(4014, "kicked")]
    [InlineData(4006, "session no longer valid")]
    [InlineData(1006, "abnormal")]
    [InlineData(null, "no close code")]
    [InlineData(4999, "unknown code")]
    public void Close_codes(int? code, string text) => Assert.Contains(text, VoiceCloseCodes.Describe(code), StringComparison.Ordinal);

    [Fact]
    public void Payload_summary_has_ids_and_modes_but_never_keys()
    {
        var d = Json("""{"mode": "aead_xchacha20_poly1305_rtpsize", "secret_key": [1,2,3], "dave_protocol_version": 1, "ssrc": 42, "modes": ["a", "b"], "user_ids": ["1", "2"], "token": "t0k"}""");
        var line = VoiceTrace.Summary(VoiceOp.SessionDescription, d);
        Assert.Equal(" ssrc=42 mode=aead_xchacha20_poly1305_rtpsize dave_protocol_version=1 user_ids=2 modes=a,b", line);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
        Assert.DoesNotContain("t0k", line, StringComparison.Ordinal);
        Assert.Equal("", VoiceTrace.Summary(VoiceOp.Hello, Json("[]")));
        Assert.Equal("", VoiceTrace.Summary(VoiceOp.Hello, Json("{}")));
    }

    [Fact]
    public void Self_voice_state_is_described_others_are_not()
    {
        var self = Json("""{"user_id": "7", "guild_id": "5", "channel_id": null, "session_id": "abcdef0123456789", "self_stream": true, "mute": false, "self_mute": false, "deaf": false, "self_deaf": true}""");
        Assert.Equal(
            "self VOICE_STATE_UPDATE guild=5 channel=none (left) session=abcdef01… self_stream=true self_video=- mute=false/false deaf=false/true",
            GatewayVoiceTrace.Describe("VOICE_STATE_UPDATE", self, 7));
        Assert.Null(GatewayVoiceTrace.Describe("VOICE_STATE_UPDATE", self, 8));
    }

    [Fact]
    public void A_voice_state_from_another_client_on_the_account_is_flagged()
    {
        var state = Json("""{"user_id": "7", "guild_id": "5", "channel_id": "9", "session_id": "phone-session-id"}""");
        Assert.EndsWith("ANOTHER DISCORD CLIENT on this account (phone/desktop/browser) owns this voice state", GatewayVoiceTrace.Describe("VOICE_STATE_UPDATE", state, 7, "bot-session-id"));
        Assert.DoesNotContain("ANOTHER", GatewayVoiceTrace.Describe("VOICE_STATE_UPDATE", state, 7, "phone-session-id"), StringComparison.Ordinal);
    }

    [Fact]
    public void Server_and_stream_events_never_carry_tokens()
    {
        var server = GatewayVoiceTrace.Describe("VOICE_SERVER_UPDATE", Json("""{"guild_id": "5", "endpoint": "c-syd.discord.media:443", "token": "SECRET"}"""), 7);
        Assert.Equal("VOICE_SERVER_UPDATE guild=5 endpoint=c-syd.discord.media:443", server);
        var streamServer = GatewayVoiceTrace.Describe("STREAM_SERVER_UPDATE", Json("""{"stream_key": "guild:5:9:7", "endpoint": "e", "token": "SECRET"}"""), 7);
        Assert.Equal("STREAM_SERVER_UPDATE stream_key=guild:5:9:7 endpoint=e", streamServer);
        Assert.Equal(
            "STREAM_DELETE stream_key=guild:5:9:7 reason=unauthorized unavailable=false",
            GatewayVoiceTrace.Describe("STREAM_DELETE", Json("""{"stream_key": "guild:5:9:7", "reason": "unauthorized", "unavailable": false}"""), 7));
        Assert.Equal(
            "STREAM_CREATE stream_key=k rtc_server_id=123 paused=false viewers=2 region=sydney",
            GatewayVoiceTrace.Describe("STREAM_CREATE", Json("""{"stream_key": "k", "rtc_server_id": "123", "paused": false, "viewer_ids": ["1","2"], "region": "sydney"}"""), 7));
        Assert.Equal("VOICE_SERVER_UPDATE guild=- endpoint=null (awaiting)", GatewayVoiceTrace.Describe("VOICE_SERVER_UPDATE", Json("""{"endpoint": null}"""), 7));
        Assert.Null(GatewayVoiceTrace.Describe("MESSAGE_CREATE", Json("{}"), 7));
        Assert.Null(GatewayVoiceTrace.Describe("STREAM_DELETE", Json("[]"), 7));
    }
}

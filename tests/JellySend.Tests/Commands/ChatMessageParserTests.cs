using System.Text.Json;
using JellySend.Commands.Routing;

namespace JellySend.Tests.Commands;

public sealed class ChatMessageParserTests
{
    private static bool Parse(string json, out ChatMessage m) => ChatMessageParser.TryParse(JsonDocument.Parse(json).RootElement, out m);

    [Fact]
    public void Parses_guild_and_dm_messages()
    {
        Assert.True(Parse("""{"content":"!ping","channel_id":"7","guild_id":"5","author":{"id":"20"}}""", out var guild));
        Assert.Equal(new ChatMessage(5, 7, 20, "!ping"), guild);
        Assert.True(Parse("""{"content":"hi","channel_id":"8","author":{"id":"20","bot":false}}""", out var dm));
        Assert.Equal(new ChatMessage(null, 8, 20, "hi"), dm);
    }

    [Theory]
    [InlineData("""{"content":"!ping","channel_id":"7","author":{"id":"20","bot":true}}""")]
    [InlineData("""{"channel_id":"7","author":{"id":"20"}}""")]
    [InlineData("""{"content":null,"channel_id":"7","author":{"id":"20"}}""")]
    [InlineData("""{"content":"x","author":{"id":"20"}}""")]
    [InlineData("""{"content":"x","channel_id":"7"}""")]
    [InlineData("""{"content":"x","channel_id":"7","author":{"id":20}}""")]
    [InlineData("""{"content":"x","channel_id":"7","author":"20"}""")]
    [InlineData("[]")]
    public void Rejects_bots_and_malformed_payloads(string json) => Assert.False(Parse(json, out _));
}

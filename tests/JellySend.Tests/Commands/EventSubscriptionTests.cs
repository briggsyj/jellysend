using JellySend.Commands.Media;
using JellySend.Commands.Routing;

namespace JellySend.Tests.Commands;

/// <summary>Spec 038: <c>!event subscribe</c> / <c>unsubscribe</c>, and admin-only commands.</summary>
public sealed class EventSubscriptionTests
{
    private readonly MediaHarness _h = new();
    private readonly FakeControl _events = new();

    public EventSubscriptionTests() => _h.Media.Events = _events;

    [Fact]
    public async Task Unsubscribe_lists_subscribed_events_and_opts_the_pick_out()
    {
        var start = _h.Time.GetUtcNow().AddDays(1);
        _events.Subscribed.AddRange([new(1, 5, "Movie Night", start), new(2, 6, "Elsewhere", start.AddDays(1))]);
        var run = _h.Start("!event unsubscribe");
        await _h.Until(() => _h.Hub.WaiterCount > 0, "the list");
        Assert.Equal(
            $"📅 **Unsubscribe from which event?** The bot won't run it. Reply with a number:\n"
            + $"`1` **Movie Night** · Movie Night — <t:{start.ToUnixTimeSeconds()}:f>\n"
            + $"`2` **Elsewhere** · a server — <t:{start.AddDays(1).ToUnixTimeSeconds()}:f>",
            _h.Texts[^1]);
        await _h.Answer("2");
        await run;
        Assert.Equal([(2UL, false)], _events.Changes);
        Assert.Equal("🚫 Unsubscribed from **Elsewhere** — the bot won't run it, and no longer shows as Interested.", _h.Texts[^1]);
    }

    [Fact]
    public async Task Subscribe_lists_the_opted_out_ones()
    {
        _events.Unsubscribed.Add(new(3, 5, "Late Show", _h.Time.GetUtcNow().AddHours(5)));
        var run = _h.Start("!events sub");
        await _h.Answer("1");
        await run;
        Assert.Equal([(3UL, true)], _events.Changes);
        Assert.StartsWith("✅ Subscribed to **Late Show**", _h.Texts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_lists_bare_help_and_unsaved_choices()
    {
        await _h.Send("!event subscribe");
        await _h.Send("!event unsubscribe");
        await _h.Send("!event");
        Assert.Equal("📭 No unsubscribed events — every upcoming event with a list is already subscribed.", _h.Texts[0]);
        Assert.StartsWith("📭 No subscribed events", _h.Texts[1], StringComparison.Ordinal);
        Assert.StartsWith("**`!event`**", _h.Texts[2], StringComparison.Ordinal);

        _events.Saves = false;
        _events.Subscribed.Add(new(1, 5, "Movie Night", _h.Time.GetUtcNow().AddDays(1)));
        var run = _h.Start("!event unsub");
        await _h.Answer("1");
        await run;
        Assert.EndsWith("an admin should check the data volume.", _h.Texts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_scheduler_nothing_is_listed()
    {
        _h.Media.Events = null;
        await _h.Send("!event unsubscribe");
        Assert.StartsWith("📭 No subscribed events", _h.Texts[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("!debug on")]
    [InlineData("!media debug")]
    public async Task Debug_is_for_admins_only(string command)
    {
        await _h.Send(command);                                      // a friend
        Assert.Equal([CommandRouter.DeniedReply], _h.Texts);
        await _h.Send(command, author: 10);                          // an admin
        Assert.NotEqual(CommandRouter.DeniedReply, _h.Texts[^1]);
    }

    private sealed class FakeControl : MediaModule.IEventControl
    {
        public List<MediaModule.EventChoice> Subscribed { get; } = [];

        public List<MediaModule.EventChoice> Unsubscribed { get; } = [];

        public List<(ulong Id, bool Subscribed)> Changes { get; } = [];

        public bool Saves { get; set; } = true;

        public IReadOnlyList<MediaModule.EventChoice> Choices(bool subscribed) => subscribed ? Subscribed : Unsubscribed;

        public bool SetSubscribed(ulong eventId, bool subscribed)
        {
            Changes.Add((eventId, subscribed));
            return Saves;
        }
    }
}

using JellySend.Commands.Media;
using JellySend.Commands.Routing;
using JellySend.Common.Permissions;
using JellySend.Tests.Discord;

namespace JellySend.Tests.Commands;

/// <summary>Spec 022: hidden phrases that run a command after an extra, themed reply.</summary>
public sealed class SecretCommandTests
{
    private readonly MediaHarness _h = new();

    public SecretCommandTests() => _h.Library.Search["sample film"] = [MediaHarness.Movie("m1", "Sample Film")];

    /// <summary>A secret reply's own line, after checking a scream follows it on the second (and last) line.</summary>
    private static string Line(string reply)
    {
        var lines = reply.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains(SecretCatalog.Uncurse(lines[1]), SecretCatalog.Screams.Select(SecretCatalog.Uncurse));
        return SecretCatalog.Uncurse(lines[0]);
    }

    [Fact]
    public async Task I_call_upon_summons_then_plays()
    {
        await _h.Send("!I call upon sample film");
        Assert.Contains(Line(_h.Texts[0]), SecretCatalog.Summons.Select(s => string.Format(System.Globalization.CultureInfo.InvariantCulture, s, "SAMPLE FILM")));
        Assert.Equal("Sample Film (2010)", _h.Current.Request.Title);
        Assert.Equal(["📡 Tuning in to **Sample Film (2010)**…", "📺 Now showing: **Sample Film (2010)**"], _h.Texts.Skip(1));
    }

    [Theory]
    [InlineData(0, "THE SEAL IS UNBROKEN, COME HENCEFORTH BIG BUCK BUNNY!")]
    [InlineData(1, "THE RIFT, THE RIFT IT OPENS!")]
    [InlineData(2, "CALL BIG BUCK BUNNY - UP, UP FROM THE DEPTHS!")]
    public void Summons_pick_one_of_three(int pick, string expected) =>
        Assert.Equal(expected, SecretCatalog.Summon(" big buck bunny ", _ => pick));

    [Fact]
    public void Every_secret_reply_ends_with_the_picked_scream()
    {
        var secrets = SecretCatalog.All(n => n - 1); // always the last of each list
        Assert.All(secrets, s => Assert.EndsWith("\n" + SecretCatalog.Uncurse(SecretCatalog.Screams[^1]), SecretCatalog.Uncurse(s.Reply("x")), StringComparison.Ordinal));
        Assert.Equal(10, SecretCatalog.Screams.Count);
    }

    [Fact]
    public void Curses_are_medium_fresh_and_leave_spaces_and_the_words_alone()
    {
        const string text = "THE GATE CLOSES";
        var first = SecretCatalog.Curse(text, new Random(1));
        var second = SecretCatalog.Curse(text, new Random(2));
        Assert.NotEqual(first, second);                                    // fresh marks each time
        Assert.Equal(text, SecretCatalog.Uncurse(first));                   // the same words underneath
        Assert.Equal(text.Count(c => c == ' '), first.Count(c => c == ' ')); // nothing stacked on spaces
        Assert.InRange(first.Length, text.Length + 1, text.Length * (1 + 3 * SecretCatalog.Intensity));
        Assert.Equal("", SecretCatalog.Curse(""));
    }

    [Fact]
    public async Task A_cursed_reply_stays_within_discords_limit()
    {
        await _h.Send("!I call upon " + new string('x', 3000));
        Assert.All(_h.Texts, t => Assert.True(t.Length <= 2000, $"{t.Length} characters"));
    }

    [Fact]
    public void A_summons_without_a_title_names_nothing() => Assert.Equal("THE RIFT, THE RIFT IT OPENS!", SecretCatalog.Summon("", _ => 0));

    [Fact]
    public async Task Every_phrase_replies_first_then_runs_its_command()
    {
        await _h.Send("!play sample film");
        _h.Sender.Sent.Clear();
        await _h.Send("!HALT THEE");
        Assert.Equal("As you command!", Line(_h.Texts[0]));
        Assert.StartsWith("⏸ Paused at", _h.Texts[1], StringComparison.Ordinal);

        _h.Sender.Sent.Clear();
        await _h.Send("!temporal   warp 0:10:00");
        Assert.Equal(["TIME, SHIFT AND BEND 0:10:00", "⏩ Jumping to 0:10:00"], [Line(_h.Texts[0]), _h.Texts[1]]);

        _h.Sender.Sent.Clear();
        await _h.Send("!temporal warp");
        Assert.Equal("TIME, SHIFT AND BEND", Line(_h.Texts[0]));
        Assert.StartsWith("⏱ ", _h.Texts[1], StringComparison.Ordinal);

        _h.Sender.Sent.Clear();
        await _h.Send("!transmogrify 16:9");
        Assert.Equal("TWIST AND SHIFT", Line(_h.Texts[0]));
        Assert.StartsWith("📐 Aspect ratio set to **16:9**", _h.Texts[1], StringComparison.Ordinal);

        _h.Sender.Sent.Clear();
        await _h.Send("!halt and cease");
        Assert.Equal(["THE GATE CLOSES", JellySend.Commands.Voice.VoiceModule.StoppedReply], [Line(_h.Texts[0]), _h.Texts[1]]);

        _h.Sender.Sent.Clear();
        await _h.Send("!raise to the ground");
        Assert.Equal(["BURN IT ALL", JellySend.Commands.Voice.VoiceModule.LeftReply], [Line(_h.Texts[0]), _h.Texts[1]]);

        _h.Sender.Sent.Clear();
        await _h.Send("!summon the order");
        Assert.Equal(["I have come", "📡 Joined — ready to broadcast."], [Line(_h.Texts[0]), _h.Texts[1]]);
    }

    [Theory]
    [InlineData("!the written tongue", "THE ACCURSED TEXTS")]
    [InlineData("!the sacred tongue", "YOUR EARS ARE UNWORTHY")]
    [InlineData("!twist the obomination", "RIP, TEAR, CONTORT")]
    public async Task Track_phrases_reply_then_run(string phrase, string reply)
    {
        await _h.Send(phrase);
        Assert.Equal(reply, Line(_h.Texts[0]));
        Assert.Equal(2, _h.Texts.Count); // then the command's own reply (nothing is playing)
    }

    [Theory]
    [InlineData("!I call")]                  // half a phrase
    [InlineData("!halt")]
    [InlineData("!halt thee!")]               // the last word must match whole
    [InlineData("!help I call upon")]
    [InlineData("!media halt thee")]
    public async Task Partial_or_listed_forms_are_not_secrets(string content)
    {
        await _h.Send(content);
        Assert.DoesNotContain(_h.Texts, t => SecretCatalog.Uncurse(t).StartsWith("As you command!", StringComparison.Ordinal));
        Assert.DoesNotContain(_h.Texts, t => SecretCatalog.Summons.Any(s => SecretCatalog.Uncurse(t).StartsWith(s[..10], StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Secrets_follow_the_commands_role_and_are_never_listed()
    {
        await _h.Send("!halt thee", author: 30); // a viewer: pause needs a friend
        Assert.Equal([CommandRouter.DeniedReply], _h.Texts);
        Assert.Null(CommandCatalog.Find("transmogrify"));
        var everything = string.Join('\n', [MediaText.HelpText, .. CommandCatalog.FullGuide()]);
        Assert.All(SecretCatalog.All(), s => Assert.DoesNotContain(s.Phrase, everything, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Router_rejects_a_secret_for_an_unknown_command()
    {
        var router = new CommandRouter(new FakeRoles(), new FakeSender(), new MessageHub(new ManualTimeProvider()));
        Assert.Throws<ArgumentException>(() => router.AddSecret("open sesame", "nope", _ => ""));
    }

    [Fact]
    public async Task The_longest_phrase_wins()
    {
        var sender = new FakeSender();
        var router = new CommandRouter(new FakeRoles(), sender, new MessageHub(new ManualTimeProvider()));
        var ran = new List<string>();
        router.Add("a", Role.Viewer, c => { ran.Add("a " + c.Rest); return Task.CompletedTask; });
        router.Add("b", Role.Viewer, c => { ran.Add("b " + c.Rest); return Task.CompletedTask; });
        router.AddSecret("open", "a", _ => "short");
        router.AddSecret("open sesame", "b", _ => "long");
        await router.DispatchAsync(new ChatMessage(5, 7, 20, "!open sesame now"), TestContext.Current.CancellationToken);
        await router.DispatchAsync(new ChatMessage(5, 7, 20, "!open door"), TestContext.Current.CancellationToken);
        Assert.Equal(["b now", "a door"], ran);
        Assert.Equal(["long", "short"], sender.Texts);
    }
}

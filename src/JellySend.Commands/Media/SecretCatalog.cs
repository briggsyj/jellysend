namespace JellySend.Commands.Media;

/// <summary>
/// Secret commands (spec 022): hidden phrases that run a normal command after an extra, themed reply. Never listed in
/// the help or the README; <c>!help &lt;phrase&gt;</c> knows nothing of them.
/// </summary>
public static class SecretCatalog
{
    /// <summary>The three incantations of <c>!I call upon</c>; <c>{0}</c> is the title asked for.</summary>
    public static readonly IReadOnlyList<string> Summons =
    [
        "THE SEAL IS UNBROKEN, COME HENCEFORTH {0}!",
        "THE RIFT, THE RIFT IT OPENS!",
        "CALL {0} - UP, UP FROM THE DEPTHS!",
    ];

    /// <summary>One of these follows every secret reply, on the next line of the same message.</summary>
    public static readonly IReadOnlyList<string> Screams =
    [
        "AAAAAAHHH—THE REEL TURNS AND I TURN WITH IT—AAAAAAAHHH",
        "IT HUNGERS. IT HUNGERS. IT HUNGERS. IT H̷U̷N̷G̷E̷R̷S̷",
        "EEEEEEEEEEEEEEEE—DIM THE LIGHTS—EEEEEEEEEEEE",
        "NNNGHHHAAAAAA THE LENS HAS OPENED AND IT WILL NOT CLOSE",
        "HAHAHAHA—AAAAAAAAAHHHH—HAHAHAHAHA",
        "R O L L T H E F I L M R O L L T H E F I L M R O L L",
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        "IT SEES US. IT HAS ALWAYS SEEN US. FRAME BY FRAME BY FRAME BY F—",
        "GGGHHHAAAAAA the screen is so BRIGHT it is so BRIGHT IT IS SO B R I G H T",
        "SHHHHH... SHHHHHH... the projector hums... AAAAAAAAAAAAAAHHHHHH",
    ];

    /// <summary>
    /// (phrase, command it runs, extra reply given the words after the phrase): the phrase's own line, then a random
    /// scream on the next line.
    /// </summary>
    public static IReadOnlyList<(string Phrase, string Command, Func<string, string> Reply)> All(Func<int, int>? pick = null)
    {
        pick ??= Random.Shared.Next;
        Func<string, string> Scream(Func<string, string> line) =>
            rest => line(rest) + "\n" + Screams[pick(Screams.Count)];

        (string, string, Func<string, string>)[] secrets =
        [
            ("I call upon", "play", rest => Summon(rest, pick)),
            ("halt thee", "pause", _ => "As you command!"),
            ("temporal warp", "time", rest => $"TIME, SHIFT AND BEND {rest}".TrimEnd()),
            ("halt and cease", "stop", _ => "THE GATE CLOSES"),
            ("raise to the ground", "leave", _ => "BURN IT ALL"),
            ("summon the order", "join", _ => "I have come"),
            ("the written tongue", "subtitles", _ => "THE ACCURSED TEXTS"),
            ("the sacred tongue", "audio", _ => "YOUR EARS ARE UNWORTHY"),
            ("twist the obomination", "language", _ => "RIP, TEAR, CONTORT"),
            ("transmogrify", "aspect-ratio", _ => "TWIST AND SHIFT"),
        ];
        return [.. secrets.Select(s => (s.Item1, s.Item2, Scream(s.Item3)))];
    }

    /// <summary>A random incantation naming <paramref name="title"/>; with no title, the one that names nothing.</summary>
    public static string Summon(string title, Func<int, int> pick)
    {
        title = title.Trim();
        return title.Length == 0
            ? Summons[1]
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, Summons[pick(Summons.Count)], title.ToUpperInvariant());
    }
}

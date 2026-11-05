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
    /// <remarks>Both lines are cursed (medium Zalgo), with fresh marks every time.</remarks>
    public static IReadOnlyList<(string Phrase, string Command, Func<string, string> Reply)> All(Func<int, int>? pick = null)
    {
        pick ??= Random.Shared.Next;
        Func<string, string> Scream(Func<string, string> line) =>
            rest => Curse(line(rest)) + "\n" + Curse(Screams[pick(Screams.Count)]);

        (string, string, Func<string, string>)[] secrets =
        [
            ("I call upon", "play", rest => Summon(rest, pick)),
            ("I summon thee", "play", rest => Summon(rest, pick)),
            ("halt thee", "pause", _ => "As you command!"),
            ("temporal warp", "time", rest => $"TIME, SHIFT AND BEND {rest}".TrimEnd()),
            ("halt and cease", "stop", _ => "THE GATE CLOSES"),
            ("raise to the ground", "leave", _ => "BURN IT ALL"),
            ("summon the order", "join", _ => "I have come"),
            ("the written tongue", "subtitles", _ => "THE ACCURSED TEXTS"),
            ("the sacred tongue", "audio", _ => "YOUR EARS ARE UNWORTHY"),
            ("twist the abomination", "language", _ => "RIP, TEAR, CONTORT"),
            ("transmogrify", "aspect-ratio", _ => "TWIST AND SHIFT"),
        ];
        return [.. secrets.Select(s => (s.Item1, s.Item2, Scream(s.Item3)))];
    }

    /// <summary>The longest title an incantation repeats (cursing multiplies its length; Discord allows 2000).</summary>
    public const int MaxTitle = 80;

    /// <summary>A random incantation naming <paramref name="title"/>; with no title, the one that names nothing.</summary>
    public static string Summon(string title, Func<int, int> pick)
    {
        title = title.Trim();
        if (title.Length > MaxTitle)
        {
            title = title[..MaxTitle].TrimEnd() + "…";
        }

        return title.Length == 0
            ? Summons[1]
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, Summons[pick(Summons.Count)], title.ToUpperInvariant());
    }

    // Combining marks that stack above, through and below a letter ("Zalgo"; lingojam's Cursed Text).
    private static readonly char[] Above =
    [
        '\u0300', '\u0301', '\u0302', '\u0303', '\u0304', '\u0306', '\u0307', '\u0308', '\u030A', '\u030B', '\u030C',
        '\u030D', '\u030E', '\u030F', '\u0310', '\u0311', '\u0312', '\u0313', '\u0314', '\u033D', '\u033E', '\u033F',
        '\u0346', '\u034A', '\u034B', '\u034C', '\u0350', '\u0351', '\u0352', '\u0357', '\u035B', '\u0363', '\u0364',
        '\u0365', '\u0366', '\u0367', '\u0368', '\u0369', '\u036A', '\u036B', '\u036C', '\u036D', '\u036E', '\u036F',
    ];

    private static readonly char[] Through =
    [
        '\u0315', '\u031B', '\u0321', '\u0322', '\u0327', '\u0328', '\u0334', '\u0335', '\u0336', '\u0337', '\u0338',
        '\u0358', '\u0489',
    ];

    private static readonly char[] Below =
    [
        '\u0316', '\u0317', '\u0318', '\u0319', '\u031C', '\u031D', '\u031E', '\u031F', '\u0320', '\u0323', '\u0324',
        '\u0325', '\u0326', '\u0329', '\u032A', '\u032B', '\u032C', '\u032D', '\u032E', '\u032F', '\u0330', '\u0331',
        '\u0332', '\u0333', '\u0339', '\u033A', '\u033B', '\u033C', '\u0345', '\u0347', '\u0348', '\u0349', '\u034D',
        '\u034E', '\u0353', '\u0354', '\u0355', '\u0356', '\u0359', '\u035A',
    ];

    /// <summary>Most marks per letter from each group: medium — clearly cursed, still readable.</summary>
    public const int Intensity = 3;

    /// <summary>
    /// Curse <paramref name="text"/>: 0–<see cref="Intensity"/> random marks above, through and below every letter (not
    /// spaces), fresh each call.
    /// </summary>
    public static string Curse(string text, Random? random = null)
    {
        random ??= Random.Shared;
        var cursed = new System.Text.StringBuilder(text.Length * 6);
        foreach (var c in text)
        {
            cursed.Append(c);
            if (char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            foreach (var marks in (char[][])[Above, Through, Below])
            {
                for (var n = random.Next(Intensity + 1); n > 0; n--)
                {
                    cursed.Append(marks[random.Next(marks.Length)]);
                }
            }
        }

        return cursed.ToString();
    }

    /// <summary><paramref name="text"/> without combining marks (what a cursed line says underneath).</summary>
    public static string Uncurse(string text) => new([.. text.Where(c => char.GetUnicodeCategory(c)
        is not (System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark))]);
}

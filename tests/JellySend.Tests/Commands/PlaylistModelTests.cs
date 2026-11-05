using JellySend.Commands.Media;

namespace JellySend.Tests.Commands;

public sealed class PlaylistModelTests
{
    private sealed record Item(string Name);

    private static Playlist<Item> Make(params string[] names)
    {
        var p = new Playlist<Item>(i => i.Name);
        foreach (var n in names)
        {
            p.Add(new Item(n));
        }

        return p;
    }

    private static IEnumerable<string> Names(Playlist<Item> p) => p.Snapshot().Select(i => i.Name);

    [Fact]
    public void Add_caps_at_ten_and_take_next_is_fifo()
    {
        var p = Make();
        for (var i = 1; i <= 10; i++)
        {
            Assert.Equal(i, p.Add(new Item($"i{i}")));
        }

        Assert.True(p.IsFull);
        Assert.Null(p.Add(new Item("x")));
        Assert.Equal("i1", p.TakeNext()!.Name);
        Assert.Equal(9, p.Count);
        p.Clear();
        Assert.Null(p.TakeNext());
    }

    [Theory]
    [InlineData("3 1", "↕️ Moved **Gamma** to #1.", "Gamma,Alpha,Beta")]
    [InlineData("alp 3", "↕️ Moved **Alpha** to #3.", "Beta,Gamma,Alpha")]
    [InlineData("the  big   gamma 2", "🔎 Nothing on the playlist matches `the  big   gamma`.", "Alpha,Beta,Gamma")]
    [InlineData("a 2", "🙅 `a` matches several items — use its number from `!playlist`.", "Alpha,Beta,Gamma")]
    [InlineData("4 1", "🙅 There's no #4 on the playlist (1–3).", "Alpha,Beta,Gamma")]
    [InlineData("0 1", "🙅 There's no #0 on the playlist (1–3).", "Alpha,Beta,Gamma")]
    [InlineData("1 4", "🙅 The new position must be between 1 and 3.", "Alpha,Beta,Gamma")]
    [InlineData("1 0", "🙅 The new position must be between 1 and 3.", "Alpha,Beta,Gamma")]
    [InlineData("1", "🙅 Say which item and where to — e.g. `!playlist order 3 1` (item 3 to the top).", "Alpha,Beta,Gamma")]
    [InlineData("1 x", "🙅 Say which item and where to — e.g. `!playlist order 3 1` (item 3 to the top).", "Alpha,Beta,Gamma")]
    [InlineData("", "🙅 Say which item and where to — e.g. `!playlist order 3 1` (item 3 to the top).", "Alpha,Beta,Gamma")]
    public void Move(string args, string reply, string after)
    {
        var p = Make("Alpha", "Beta", "Gamma");
        Assert.Equal(reply, p.Move(args));
        Assert.Equal(after.Split(','), Names(p));
    }

    [Theory]
    [InlineData("2", "➖ Took **Beta** off the playlist.", "Alpha,Gamma")]
    [InlineData("GAMMA", "➖ Took **Gamma** off the playlist.", "Alpha,Beta")]
    [InlineData("zzz", "🔎 Nothing on the playlist matches `zzz`.", "Alpha,Beta,Gamma")]
    [InlineData("  ", "🙅 Say which item — e.g. `!playlist remove 2`.", "Alpha,Beta,Gamma")]
    public void Remove(string target, string reply, string after)
    {
        var p = Make("Alpha", "Beta", "Gamma");
        Assert.Equal(reply, p.Remove(target));
        Assert.Equal(after.Split(','), Names(p));
    }

    [Fact]
    public void Empty_playlist_replies()
    {
        Assert.Equal("📭 The playlist is empty.", Make().Move("1 1"));
        Assert.Equal("📭 The playlist is empty.", Make().Remove("1"));
    }
}

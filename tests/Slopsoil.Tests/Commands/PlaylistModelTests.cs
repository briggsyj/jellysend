using Slopsoil.Commands.Media;

namespace Slopsoil.Tests.Commands;

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
    [InlineData("3 1", "moved **Gamma** to #1", "Gamma,Alpha,Beta")]
    [InlineData("alp 3", "moved **Alpha** to #3", "Beta,Gamma,Alpha")]
    [InlineData("the  big   gamma 2", "nothing in the playlist matches `the  big   gamma`", "Alpha,Beta,Gamma")]
    [InlineData("a 2", "`a` matches several items — use its number from `!playlist`", "Alpha,Beta,Gamma")]
    [InlineData("4 1", "there's no #4 in the playlist (1–3)", "Alpha,Beta,Gamma")]
    [InlineData("0 1", "there's no #0 in the playlist (1–3)", "Alpha,Beta,Gamma")]
    [InlineData("1 4", "position must be between 1 and 3", "Alpha,Beta,Gamma")]
    [InlineData("1 0", "position must be between 1 and 3", "Alpha,Beta,Gamma")]
    [InlineData("1", "usage: `!order <# or title> <new #>`", "Alpha,Beta,Gamma")]
    [InlineData("1 x", "usage: `!order <# or title> <new #>`", "Alpha,Beta,Gamma")]
    [InlineData("", "usage: `!order <# or title> <new #>`", "Alpha,Beta,Gamma")]
    public void Move(string args, string reply, string after)
    {
        var p = Make("Alpha", "Beta", "Gamma");
        Assert.Equal(reply, p.Move(args));
        Assert.Equal(after.Split(','), Names(p));
    }

    [Theory]
    [InlineData("2", "removed **Beta** from the playlist", "Alpha,Gamma")]
    [InlineData("GAMMA", "removed **Gamma** from the playlist", "Alpha,Beta")]
    [InlineData("zzz", "nothing in the playlist matches `zzz`", "Alpha,Beta,Gamma")]
    [InlineData("  ", "usage: `!remove <# or title>`", "Alpha,Beta,Gamma")]
    public void Remove(string target, string reply, string after)
    {
        var p = Make("Alpha", "Beta", "Gamma");
        Assert.Equal(reply, p.Remove(target));
        Assert.Equal(after.Split(','), Names(p));
    }

    [Fact]
    public void Empty_playlist_replies()
    {
        Assert.Equal("the playlist is empty", Make().Move("1 1"));
        Assert.Equal("the playlist is empty", Make().Remove("1"));
    }
}

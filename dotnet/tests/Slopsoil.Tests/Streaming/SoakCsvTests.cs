using Slopsoil.Streaming.Diagnostics;

namespace Slopsoil.Tests.Streaming;

public sealed class SoakCsvTests
{
    [Fact]
    public void Row_matches_the_header_columns()
    {
        var s = new StreamDiagnosticsSnapshot
        {
            Position = 123.4567, Window = TimeSpan.FromSeconds(10), Frames = 300, FpsSent = 30, FpsTarget = 30, LateFrames = 2,
            LateTotalMs = 40.5, MaxLatenessMs = 30, ReadStallMs = 1.25, PacketsPerSec = 410, SendDrops = 1, SendErrors = 0,
            DriftMs = -12.5, SilenceSteps = 1, SkipSteps = 0, Encoder = "libx264, sw",
        };
        var row = SoakCsv.Row(20, s);
        Assert.Equal("20,123.457,10,300,30,30,2,40.5,30,1.25,410,1,0,-12.5,1,0,libx264; sw", row);
        Assert.Equal(SoakCsv.Header.Split(',').Length, row.Split(',').Length);
        Assert.Equal("", SoakCsv.Row(0, s with { DriftMs = null }).Split(',')[13]);
    }
}

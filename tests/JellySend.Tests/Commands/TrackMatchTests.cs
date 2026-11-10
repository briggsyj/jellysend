using JellySend.Commands.Media;
using JellySend.Services.Jellyfin;

namespace JellySend.Tests.Commands;

/// <summary>Spec 008: casual track names for <c>!subs</c> / <c>!audio</c>.</summary>
public sealed class TrackMatchTests
{
    private static readonly List<MediaTrack?> Subs =
    [
        null,
        new(3, false, "eng", "English signs", false, true),
        new(4, false, "eng", "English - SRT", false, false),
        new(5, false, "fre", "Français", false, false),
        new(6, false, "ger", "Deutsch", false, false),
    ];

    private static readonly List<MediaTrack?> Audio =
    [
        new(1, true, "eng", "English 5.1 - Default", true, false),
        new(2, true, "jpn", "Japanese - AAC", false, false),
        new(7, true, "eng", "Commentary", false, false),
    ];

    [Theory]
    [InlineData("off", 0)]
    [InlineData("None", 0)]
    [InlineData("english", 2)]     // the full track, not the forced signs
    [InlineData("eng", 2)]
    [InlineData("EN", 2)]
    [InlineData("fr", 3)]
    [InlineData("fra", 3)]         // terminology code for Jellyfin's "fre"
    [InlineData("deu", 4)]
    [InlineData("signs", 1)]
    [InlineData("3", 2)]
    [InlineData("6", null)]
    [InlineData("klingon", null)]
    [InlineData("", null)]
    public void Subtitles(string text, int? index) => Assert.Equal(index, TrackMatch.Find(Subs, text, audio: false));

    [Theory]
    [InlineData("japanese", 1)]
    [InlineData("jpn", 1)]
    [InlineData("ja", 1)]
    [InlineData("english", 0)]     // the default one first
    [InlineData("commentary", 2)]
    [InlineData("off", null)]      // audio can't be off
    public void Audio_tracks(string text, int? index) => Assert.Equal(index, TrackMatch.Find(Audio, text, audio: true));
}

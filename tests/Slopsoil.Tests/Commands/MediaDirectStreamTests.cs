using Slopsoil.Services.Jellyfin;

namespace Slopsoil.Tests.Commands;

/// <summary>Spec 006: play the original file and transcode once in the bot; Jellyfin transcodes only for subtitles.</summary>
public sealed class MediaDirectStreamTests
{
    private readonly MediaHarness _h = new();

    public MediaDirectStreamTests()
    {
        _h.Library.Direct = true;
        _h.Library.Codec = "hevc";
        _h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000)];
        _h.Library.Tracks["f"] =
        [
            new(1, true, "eng", "Commentary", false, false),
            new(2, true, "eng", "English 5.1", true, false),
            new(3, true, "jpn", "Japanese", false, false),
            new(4, false, "eng", "English", false, false),
        ];
    }

    [Fact]
    public async Task Original_file_is_streamed_with_jellyfins_default_audio_and_no_hls_resolve()
    {
        await _h.Send("!play film");
        var r = _h.Current.Request;
        Assert.Equal("http://jf/Videos/f/stream?static=true&api_key=SECRET", r.Url);
        Assert.Equal(2, r.AudioStreamIndex);   // the default track, not the first (commentary)
        Assert.Equal("hevc", r.SourceCodec);
        Assert.Equal(0, _h.Library.Resolves);
    }

    [Fact]
    public async Task Source_frame_rate_reaches_the_stream_request()
    {
        _h.Library.FrameRate = 23.976;
        await _h.Send("!play film");
        Assert.Equal(23.976, _h.Current.Request.SourceFps);
    }

    [Fact]
    public async Task Probed_frame_rate_is_used_when_jellyfin_reports_none()
    {
        _h.Library.UnknownMetadata = true;
        _h.Library.Probe = new("h264", 25.0, "high", false, 1920, 1080, true, "aac");
        await _h.Send("!play film");
        Assert.Equal(25.0, _h.Current.Request.SourceFps);
    }

    [Fact]
    public async Task Codecs_the_transcode_path_rejects_are_fine_when_direct()
    {
        _h.Library.Codec = "av1";
        await _h.Send("!play film");
        Assert.Equal("av1", _h.Current.Request.SourceCodec);
    }

    [Fact]
    public async Task Audio_choice_maps_the_input_stream_and_subtitles_switch_to_jellyfins_transcode()
    {
        await _h.Send("!play film");
        await _h.Send("!media audio 3");
        Assert.Equal(3, _h.Current.Request.AudioStreamIndex);
        Assert.StartsWith("http://jf/Videos/f/stream?static=true", _h.Current.Request.Url, StringComparison.Ordinal);

        await _h.Send("!media subs 2");    // subtitles on → Jellyfin transcodes with burn-in; it picks the audio
        Assert.Contains("variant.m3u8", _h.Current.Request.Url, StringComparison.Ordinal);
        Assert.Null(_h.Current.Request.AudioStreamIndex);
        Assert.Equal(new TrackSelection(3, 4), _h.Library.PlaybackCalls[^1].Tracks);

        await _h.Send("!media subs off");  // and back to the original file
        Assert.StartsWith("http://jf/Videos/f/stream?static=true", _h.Current.Request.Url, StringComparison.Ordinal);
        Assert.Equal(3, _h.Current.Request.AudioStreamIndex);
    }
}

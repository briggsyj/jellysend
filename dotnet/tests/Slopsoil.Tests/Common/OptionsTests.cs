using Slopsoil.Common.Config;

namespace Slopsoil.Tests.Common;

public sealed class OptionsTests
{
    private readonly List<string> _warnings = [];

    private SlopsoilOptions Parse(params (string Key, string? Value)[] vars) =>
        SlopsoilOptions.From(vars.ToDictionary(v => v.Key, v => v.Value), _warnings.Add);

    [Fact]
    public void Defaults_match_the_python_bot()
    {
        var o = Parse();
        Assert.Null(o.DiscordToken);
        Assert.Empty(o.AllowedUserIds);
        Assert.Null(o.JellyfinUrl);
        Assert.Equal(("1920:1080", 60.0, "12000k"), (o.Resolution, o.Fps, o.VideoBitrate));
        Assert.Equal((0.0, 0), (o.PacketPace, o.AvSyncMs));
        Assert.False(o.JellyfinConfigured);
        Assert.False(o.SyncPlayConfigured);
        Assert.Empty(_warnings);
    }

    [Theory]
    [InlineData("720p", "1280:720", 30.0, "10000k")]
    [InlineData(" 4K ", "3840:2160", 60.0, "24000k")]
    [InlineData("1080P", "1920:1080", 60.0, "12000k")]
    public void Quality_presets(string quality, string res, double fps, string bitrate)
    {
        var o = Parse(("STREAM_QUALITY", quality));
        Assert.Equal((res, fps, bitrate), (o.Resolution, o.Fps, o.VideoBitrate));
    }

    [Fact]
    public void Unknown_quality_warns_and_uses_1080p()
    {
        Assert.Equal("1920:1080", Parse(("STREAM_QUALITY", "8k")).Resolution);
        Assert.Contains("unknown", Assert.Single(_warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Per_axis_overrides_beat_the_preset()
    {
        var o = Parse(("STREAM_QUALITY", "720p"), ("STREAM_RESOLUTION", "960x540"), ("STREAM_FPS", "29.97"), ("STREAM_VIDEO_BITRATE", "2000k"));
        Assert.Equal(("960:540", 29.97, "2000k"), (o.Resolution, o.Fps, o.VideoBitrate));
    }

    [Theory]
    [InlineData("STREAM_RESOLUTION", "big")]
    [InlineData("STREAM_RESOLUTION", "1:2")]
    [InlineData("STREAM_FPS", "0.5")]
    [InlineData("STREAM_FPS", "61")]
    [InlineData("STREAM_FPS", "fast")]
    [InlineData("STREAM_FPS", "NaN")]
    public void Invalid_axis_values_warn_and_keep_the_preset(string key, string value)
    {
        var o = Parse((key, value));
        Assert.Equal(("1920:1080", 60.0), (o.Resolution, o.Fps));
        Assert.Single(_warnings);
    }

    [Theory]
    [InlineData("0.75", 0.75)]
    [InlineData("2", 0.95)]
    [InlineData("-1", 0.0)]
    [InlineData("x", 0.0)]
    public void Packet_pace_is_clamped(string raw, double expected) =>
        Assert.Equal(expected, Parse(("STREAM_PACKET_PACE", raw)).PacketPace);

    [Theory]
    [InlineData("120", 120)]
    [InlineData("-9000", -5000)]
    [InlineData("+9000", 5000)]
    [InlineData("99999999999", 5000)]
    [InlineData("1.5", 0)]
    public void Av_sync_is_clamped(string raw, int expected) =>
        Assert.Equal(expected, Parse(("STREAM_AV_SYNC_MS", raw)).AvSyncMs);

    [Theory]
    [InlineData(null, 20.0, 0)]
    [InlineData("5", 5.0, 0)]
    [InlineData("0", 0.0, 0)]
    [InlineData("1.5", 1.5, 0)]
    [InlineData("1440", 1440.0, 0)]
    [InlineData("1441", 20.0, 1)]
    [InlineData("-1", 20.0, 1)]
    [InlineData("soon", 20.0, 1)]
    public void Empty_channel_timeout(string? raw, double minutes, int warnings)
    {
        var o = raw is null ? Parse() : Parse(("EMPTY_CHANNEL_TIMEOUT_MINUTES", raw));
        Assert.Equal(TimeSpan.FromMinutes(minutes), o.EmptyChannelTimeout);
        Assert.Equal(warnings, _warnings.Count);
    }

    [Fact]
    public void Allowed_ids_skip_non_digits_like_isdigit()
    {
        var o = Parse(("ALLOWED_USER_IDS", " 123, abc,,-4, 456 ,7.0"));
        Assert.Equal([123UL, 456UL], o.AllowedUserIds.Order());
    }

    [Fact]
    public void Jellyfin_settings_and_flags()
    {
        var key = Parse(("JELLYFIN_URL", "http://jf:8096//"), ("JELLYFIN_API_KEY", "k"));
        Assert.Equal("http://jf:8096", key.JellyfinUrl);
        Assert.True(key.JellyfinConfigured);
        Assert.False(key.SyncPlayConfigured);

        var login = Parse(("JELLYFIN_URL", "http://jf"), ("JELLYFIN_USERNAME", "u"), ("JELLYFIN_PASSWORD", "p"), ("DISCORD_TOKEN", " tok "));
        Assert.True(login.JellyfinConfigured);
        Assert.True(login.SyncPlayConfigured);
        Assert.Equal("tok", login.DiscordToken);

        Assert.False(Parse(("JELLYFIN_URL", "http://jf"), ("JELLYFIN_USERNAME", "u")).JellyfinConfigured);
        Assert.False(Parse(("JELLYFIN_API_KEY", "k")).JellyfinConfigured);
    }

    [Fact]
    public void Out_of_subset_variables_are_ignored_and_secrets_never_printed()
    {
        var o = Parse(("TVHEADEND_URL", "http://tv"), ("TIMEZONE", "UTC"), ("YTDLP_FORMAT", "best"),
            ("DISCORD_TOKEN", "secret-token"), ("JELLYFIN_PASSWORD", "secret-pw"), ("JELLYFIN_API_KEY", "secret-key"));
        Assert.Empty(_warnings);
        Assert.DoesNotContain("secret", o.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void From_environment_reads_process_variables()
    {
        Environment.SetEnvironmentVariable("STREAM_AV_SYNC_MS", "42");
        try
        {
            Assert.Equal(42, SlopsoilOptions.FromEnvironment().AvSyncMs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STREAM_AV_SYNC_MS", null);
        }
    }
}

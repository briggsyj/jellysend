using Slopsoil.Streaming.Engine;

namespace Slopsoil.Tests.Commands;

/// <summary>Spec 012: a direct stream that never produced a frame is retried once through Jellyfin's transcode.</summary>
public sealed class MediaRetryTests
{
    private readonly MediaHarness _h = new();

    public MediaRetryTests()
    {
        _h.Library.Direct = true;
        _h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000, ticks: 7200L * 10_000_000)];
    }

    [Fact]
    public async Task Failed_direct_start_retries_through_jellyfins_transcode_at_the_same_position()
    {
        await _h.Send("!play film 1:00:00");
        Assert.Contains("static=true", _h.Current.Request.Url, StringComparison.Ordinal);
        _h.Current.Fake.Position = 3600;
        _h.Current.End(PipelineEndReason.Failed, "go-live stream failed to start");

        await _h.UntilReply("↻ retrying through Jellyfin's transcode…");
        await _h.Until(() => _h.Pipelines.Created.Count == 2, "a second pipeline");
        Assert.Equal([true, false], _h.Library.AllowDirectCalls);
        Assert.DoesNotContain("static=true", _h.Current.Request.Url, StringComparison.Ordinal);
        Assert.Equal(3600, _h.Current.Request.StartOffset);
        Assert.Contains("ps1", _h.Library.Stopped); // the direct session was released

        // Remembered: the next start of that item goes straight to the transcode.
        await _h.Send("!stop");
        await _h.Send("!play film");
        Assert.Equal([true, false, false], _h.Library.AllowDirectCalls);
    }

    [Fact]
    public async Task A_failing_transcode_retry_does_not_loop()
    {
        await _h.Send("!play film");
        _h.Current.End(PipelineEndReason.Failed, "go-live stream failed to start");
        await _h.Until(() => _h.Pipelines.Created.Count == 2, "the retry");
        _h.Current.End(PipelineEndReason.Failed, "go-live stream failed to start");
        await _h.Until(() => _h.Library.Stopped.Contains("ps2"), "the transcode session released");
        Assert.Equal(2, _h.Pipelines.Created.Count);
        Assert.Single(_h.Texts, t => t.StartsWith('↻'));
    }

    [Fact]
    public async Task Failures_after_the_first_frame_are_not_retried()
    {
        await _h.Send("!play film");
        _h.Current.Fake.FirstFrame.TrySetResult();
        _h.Current.End(PipelineEndReason.Failed, "go-live error: the video encoder failed — see the logs");
        await _h.Until(() => _h.Library.Stopped.Contains("ps1"), "the session released");
        Assert.Single(_h.Pipelines.Created);
        Assert.DoesNotContain(_h.Texts, t => t.StartsWith('↻'));
    }

    [Fact]
    public async Task Transcoded_streams_are_not_retried()
    {
        _h.Library.Direct = false;
        await _h.Send("!play film");
        _h.Current.End(PipelineEndReason.Failed, "go-live stream failed to start");
        await _h.Until(() => _h.Library.Stopped.Contains("ps1"), "the session released");
        Assert.Single(_h.Pipelines.Created);
    }
}

/// <summary>Spec 017: the stats say where the picture comes from.</summary>
public sealed class MediaTranscodeReasonTests
{
    [Fact]
    public async Task A_jellyfin_transcode_without_a_reason_still_says_so()
    {
        var h = new MediaHarness();
        h.Library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000)];
        await h.Send("!play film");
        Assert.Equal("Jellyfin chose it", h.Current.Request.JellyfinTranscodeReason);
    }
}

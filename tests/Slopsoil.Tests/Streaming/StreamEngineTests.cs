using Slopsoil.Streaming.Engine;

namespace Slopsoil.Tests.Streaming;

public sealed class StreamEngineTests
{
    private readonly FakeConnector _connector = new();
    private readonly FakePipelineFactory _pipelines;
    private readonly StreamEngine _engine;
    private readonly List<string> _sent = [];
    private int _ends;

    public StreamEngineTests()
    {
        _pipelines = new FakePipelineFactory(_connector.Log);
        _engine = new StreamEngine(_connector, _pipelines);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StreamRequest Req(string title = "Film") => new() { Title = title, Url = "http://jf/x?api_key=secret", StartOffset = 5 };

    private Task Send(string s)
    {
        lock (_sent)
        {
            _sent.Add(s);
        }

        return Task.CompletedTask;
    }

    private readonly List<PipelineEndReason> _endReasons = [];
    private PipelineEnd? _lastEnd;

    private Task OnEnd(PipelineEnd end)
    {
        lock (_endReasons)
        {
            _endReasons.Add(end.Reason);
            _lastEnd = end;
        }

        Interlocked.Increment(ref _ends);
        return Task.CompletedTask;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task Start_connects_go_live_and_runs_the_pipeline()
    {
        Assert.True(await _engine.StartAsync(5, Req(), Send, OnEnd, Ct));
        Assert.Equal(["📺 **Film** (go-live)"], _sent);
        Assert.Equal(["connect 5", "start Film"], _connector.Log);
        Assert.True(_engine.IsStreaming(5));
        Assert.Equal(5.0, _engine.GetPlayback(5)!.Position);
        Assert.Null(_engine.GetPlayback(6));
    }

    [Fact]
    public async Task Natural_end_cleans_up_then_calls_on_end()
    {
        await _engine.StartAsync(5, Req(), Send, OnEnd, Ct);
        _pipelines.Created[0].End(PipelineEndReason.Natural);
        await Until(() => _ends == 1);
        Assert.False(_engine.IsStreaming(5));
        Assert.Equal(1, _connector.Links[0].Disconnects);
        Assert.Equal(1, _pipelines.Created[0].Stops);
    }

    [Fact]
    public async Task Stop_ends_without_on_end_and_is_idempotent()
    {
        await _engine.StartAsync(5, Req(), Send, OnEnd, Ct);
        await _engine.StopAsync(5);
        await _engine.StopAsync(5);
        await _engine.StopAsync(9);
        Assert.False(_engine.IsStreaming(5));
        Assert.Equal(1, _connector.Links[0].Disconnects);
        await Task.Delay(50, Ct);
        Assert.Equal(0, _ends);
    }

    [Fact]
    public async Task Replacing_waits_for_the_old_cleanup_before_connecting_again()
    {
        await _engine.StartAsync(5, Req("One"), Send, OnEnd, Ct);
        await _engine.StartAsync(5, Req("Two"), Send, OnEnd, Ct);
        Assert.Equal(["connect 5", "start One", "stop One", "disconnect", "connect 5", "start Two"], _connector.Log);
        Assert.Equal("Two", _pipelines.Created[1].Request.Title);
        Assert.True(_engine.IsStreaming(5));
        await Task.Delay(50, Ct);
        Assert.Equal(0, _ends); // the replaced stream did not "end naturally"
    }

    [Fact]
    public async Task Failures_reply_and_release_the_guild()
    {
        _pipelines.EncoderAvailable = false;
        Assert.False(await _engine.StartAsync(5, Req(), Send, OnEnd, Ct));
        _pipelines.EncoderAvailable = true;

        _connector.Next.Enqueue(_ => Task.FromException<Slopsoil.Streaming.Engine.IGoLiveLink>(new TimeoutException("no STREAM_CREATE")));
        Assert.False(await _engine.StartAsync(5, Req(), Send, OnEnd, Ct));
        Assert.False(_engine.IsStreaming(5));

        _pipelines.ThrowOnCreate = new InvalidOperationException("ffmpeg missing");
        Assert.False(await _engine.StartAsync(5, Req(), Send, OnEnd, Ct));
        Assert.False(_engine.IsStreaming(5));
        Assert.Equal(1, _connector.Links[0].Disconnects);

        Assert.Equal(
            [
                "go-live requires a working H.264 encoder; none found",
                "📺 **Film** (go-live)", "go-live connection failed: no STREAM_CREATE",
                "📺 **Film** (go-live)", "go-live error: ffmpeg missing",
            ],
            _sent);
    }

    [Fact]
    public async Task Pipeline_failure_replies_with_its_error_and_reports_the_end()
    {
        await _engine.StartAsync(5, Req(), Send, OnEnd, Ct);
        _pipelines.Created[0].End(PipelineEndReason.Failed, "go-live stream failed to start");
        await Until(() => _ends == 1);
        Assert.Equal("go-live stream failed to start", _sent[1]);
        Assert.False(_engine.IsStreaming(5));
        Assert.Equal([PipelineEndReason.Failed], _endReasons); // like Python: any unstopped end may auto-advance
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_end_reports_whether_a_frame_was_sent_and_where(bool sent)
    {
        await _engine.StartAsync(5, Req(), Send, OnEnd, Ct);
        _pipelines.Created[0].Fake.Position = 3600;
        if (sent)
        {
            _pipelines.Created[0].Fake.FirstFrame.TrySetResult();
        }

        _pipelines.Created[0].End(PipelineEndReason.Failed, "x");
        await Until(() => _ends == 1);
        Assert.Equal((sent, (double?)3600), (_lastEnd!.FirstFrameSent, _lastEnd.Position));
    }

    [Fact]
    public async Task Go_live_connection_loss_stops_the_pipeline_and_reports_the_position()
    {
        await _engine.StartAsync(5, Req(), Send, OnEnd, Ct);
        _pipelines.Created[0].Fake.Position = 42.5;
        _connector.Links[0].Close();
        await Until(() => _ends == 1);
        Assert.Equal(PipelineEndReason.ConnectionLost, _lastEnd!.Reason);
        Assert.Equal(42.5, _lastEnd.Position);
        Assert.True(_pipelines.Created[0].Stops >= 1); // stopped before the end is reported (cleanup may repeat it)
        Assert.Equal(1, _connector.Links[0].Disconnects);
        Assert.Contains(StreamEngine.ConnectionLostMessage, _sent);
        Assert.False(_engine.IsStreaming(5));
    }

    [Fact]
    public async Task A_failing_reply_does_not_leak_the_session()
    {
        Task Throwing(string _) => throw new InvalidOperationException("REST 403");
        Assert.True(await _engine.StartAsync(5, Req(), Throwing, OnEnd, Ct));
        await _engine.StopAsync(5);
        Assert.False(_engine.IsStreaming(5));
        _connector.Next.Enqueue(_ => Task.FromException<IGoLiveLink>(new TimeoutException("x")));
        Assert.False(await _engine.StartAsync(5, Req(), Throwing, OnEnd, Ct));
        Assert.False(_engine.IsStreaming(5));
        Assert.Empty(_engine.ActiveGuilds);
    }

    [Fact]
    public async Task Stop_racing_pipeline_creation_still_cleans_up()
    {
        var gate = new SemaphoreSlim(0);
        var engine = new StreamEngine(_connector, new BlockingFactory(_pipelines, gate));
        var start = Task.Run(() => engine.StartAsync(5, Req(), Send, OnEnd, Ct), Ct); // blocks inside Create
        await Until(() => _connector.Links.Count == 1);
        var stop = engine.StopAsync(5);           // sees Pipeline == null
        gate.Release();                           // creation proceeds after the stop flag is set
        Assert.False(await start);
        await stop;
        Assert.False(engine.IsStreaming(5));
        Assert.Equal(1, _pipelines.Created[0].Stops);
        Assert.Equal(1, _connector.Links[0].Disconnects);
    }

    private sealed class BlockingFactory(FakePipelineFactory inner, SemaphoreSlim gate) : IPipelineFactory
    {
        public bool EncoderAvailable => true;

        public IPipeline Create(StreamRequest request, IGoLiveLink link)
        {
            gate.Wait(TestContext.Current.CancellationToken);
            return inner.Create(request, link);
        }
    }

    [Fact]
    public async Task Stop_while_connecting_cancels_the_connect()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connector.Next.Enqueue(async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var start = _engine.StartAsync(5, Req(), Send, OnEnd, Ct);
        await entered.Task;
        Assert.True(_engine.IsStreaming(5));
        await _engine.StopAsync(5);
        Assert.False(await start);
        Assert.False(_engine.IsStreaming(5));
        Assert.Empty(_pipelines.Created);
    }

    [Fact]
    public async Task Stop_racing_a_connect_that_succeeds_disconnects_the_new_link()
    {
        var release = new TaskCompletionSource<Slopsoil.Streaming.Engine.IGoLiveLink>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = new FakeLink();
        _connector.Next.Enqueue(_ => release.Task); // ignores cancellation
        var start = _engine.StartAsync(5, Req(), Send, OnEnd, Ct);
        var stop = _engine.StopAsync(5);
        release.SetResult(link);
        Assert.False(await start);
        await stop;
        Assert.Equal(1, link.Disconnects);
        Assert.Empty(_pipelines.Created);
    }

    [Fact]
    public async Task End_callback_and_send_errors_are_contained()
    {
        await _engine.StartAsync(5, Req(), Send, _ => throw new InvalidOperationException("cb"), Ct);
        _pipelines.Created[0].End(PipelineEndReason.Natural);
        await Until(() => !_engine.IsStreaming(5));

        await _engine.StartAsync(6, Req(), s => s.StartsWith("📺", StringComparison.Ordinal) ? Task.CompletedTask : throw new InvalidOperationException("send"), OnEnd, Ct);
        _pipelines.Created[1].End(PipelineEndReason.Failed, "boom");
        await Until(() => !_engine.IsStreaming(6));
    }
}

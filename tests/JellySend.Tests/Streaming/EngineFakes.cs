using JellySend.Discord.Dave;
using JellySend.Streaming.Engine;

namespace JellySend.Tests.Streaming;

internal sealed class FakeLink : IGoLiveLink
{
    public int Disconnects { get; private set; }

    public List<string>? Log { get; init; }

    public string Mode => "aead_xchacha20_poly1305_rtpsize";

    public byte[] SecretKey { get; } = new byte[32];

    public uint AudioSsrc => 100;

    public uint VideoSsrc => 101;

    public IDaveSession? Dave => null;

    public bool SendPacket(ReadOnlySpan<byte> datagram) => true;

    public Task DisconnectAsync()
    {
        Disconnects++;
        Log?.Add("disconnect");
        return Task.CompletedTask;
    }

    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Closed => _closed.Task;

    /// <summary>Simulate the go-live media connection dropping.</summary>
    public void Close() => _closed.TrySetResult();

    public ValueTask DisposeAsync() => new(DisconnectAsync());
}

internal sealed class FakeConnector : IGoLiveConnector
{
    public Queue<Func<CancellationToken, Task<IGoLiveLink>>> Next { get; } = new();

    public List<FakeLink> Links { get; } = [];

    public List<string> Log { get; } = [];

    public Task<IGoLiveLink> ConnectAsync(ulong guildId, CancellationToken ct)
    {
        Log.Add($"connect {guildId}");
        if (Next.Count > 0)
        {
            return Next.Dequeue()(ct);
        }

        var link = new FakeLink { Log = Log };
        Links.Add(link);
        return Task.FromResult<IGoLiveLink>(link);
    }
}

internal sealed class FakePlayback : IPlayback
{
    public double Position { get; set; }

    public bool IsPaused { get; set; }

    public bool IsSourceActive { get; set; } = true;

    public double? Aspect { get; set; }

    public bool DebugOverlay { get; set; }

    public TaskCompletionSource FirstFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstFrameSent => FirstFrame.Task;

    public List<string> Calls { get; } = [];

    public void Pause()
    {
        IsPaused = true;
        Calls.Add("pause");
    }

    public void Resume()
    {
        IsPaused = false;
        Calls.Add("resume");
    }

    public void Seek(double seconds)
    {
        Position = seconds;
        Calls.Add($"seek {seconds}");
    }

    public void SetAspect(double? aspect)
    {
        Aspect = aspect;
        Calls.Add($"aspect {aspect}");
    }

    public void SetDebugOverlay(bool on)
    {
        DebugOverlay = on;
        Calls.Add($"debug {on}");
    }

    public string DiagnosticsText() => "diag";
}

internal sealed class FakePipeline(StreamRequest request, List<string> log) : IPipeline
{
    private readonly TaskCompletionSource<PipelineEnd> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StreamRequest Request { get; } = request;

    public FakePlayback Fake { get; } = new() { Position = request.StartOffset, Aspect = request.Aspect, DebugOverlay = request.DebugOverlay };

    public IPlayback Playback => Fake;

    public bool Started { get; private set; }

    public int Stops { get; private set; }

    public Task<PipelineEnd> Completion => _end.Task;

    public void Start()
    {
        Started = true;
        log.Add($"start {Request.Title}");
    }

    public Task StopAsync()
    {
        Stops++;
        log.Add($"stop {Request.Title}");
        Fake.IsSourceActive = false;
        _end.TrySetResult(new PipelineEnd(PipelineEndReason.Stopped));
        return Task.CompletedTask;
    }

    public void End(PipelineEndReason reason, string? error = null)
    {
        Fake.IsSourceActive = false;
        _end.TrySetResult(new PipelineEnd(reason, error));
    }
}

internal sealed class FakePipelineFactory(List<string> log) : IPipelineFactory
{
    public bool EncoderAvailable { get; set; } = true;

    public List<FakePipeline> Created { get; } = [];

    public Exception? ThrowOnCreate { get; set; }

    public IPipeline Create(StreamRequest request, IGoLiveLink link)
    {
        if (ThrowOnCreate is { } ex)
        {
            throw ex;
        }

        var p = new FakePipeline(request, log);
        Created.Add(p);
        return p;
    }
}

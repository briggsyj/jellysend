using Slopsoil.Discord.Dave;

namespace Slopsoil.Streaming.Engine;

/// <summary>A go-live (screenshare) connection the pipeline sends media through.</summary>
public interface IGoLiveLink : IAsyncDisposable
{
    string Mode { get; }

    byte[] SecretKey { get; }

    uint AudioSsrc { get; }

    uint VideoSsrc { get; }

    /// <summary>The go-live connection's DAVE session (null without DAVE).</summary>
    IDaveSession? Dave { get; }

    bool SendPacket(ReadOnlySpan<byte> datagram);

    /// <summary>STREAM_DELETE + close. Idempotent.</summary>
    Task DisconnectAsync();

    /// <summary>Completes when the go-live media connection closed on its own (server restart, migration, kick).</summary>
    Task Closed => Task.Delay(Timeout.Infinite);
}

/// <summary>Starts go-live in the guild's current voice channel (Host: VoiceConnection → GoLiveConnection).</summary>
public interface IGoLiveConnector
{
    Task<IGoLiveLink> ConnectAsync(ulong guildId, CancellationToken ct);
}

/// <summary>What to play.</summary>
public sealed record StreamRequest
{
    public required string Title { get; init; }

    /// <summary>Input URL (may carry credentials; only logged redacted).</summary>
    public required string Url { get; init; }

    public bool HasAudio { get; init; } = true;

    /// <summary>Absolute input stream index of the audio to play (direct stream of the original file); null = first.</summary>
    public int? AudioStreamIndex { get; init; }

    public bool? Live { get; init; } = true;

    public long ProbeSize { get; init; } = 2_000_000;

    public double StartOffset { get; init; }

    public double? Aspect { get; init; }

    public bool DebugOverlay { get; init; }

    /// <summary>Source video codec, for diagnostics.</summary>
    public string? SourceCodec { get; init; }

    /// <summary>Item runtime in seconds when known, for diagnostics.</summary>
    public double? RuntimeSeconds { get; init; }

    /// <summary>Source video frame rate when known; the output follows it up to <c>STREAM_FPS</c> (spec 007).</summary>
    public double? SourceFps { get; init; }

    /// <summary>
    /// Output height chosen at runtime (spec 010, <c>!resolution</c>); null = the configured resolution. The box is
    /// 16:9 for the height and the bitrate scales down with the pixel count.
    /// </summary>
    public int? OutputHeight { get; init; }
}

/// <summary>Live controls of a running stream (the video player, as the commands see it).</summary>
public interface IPlayback
{
    double Position { get; }

    bool IsPaused { get; }

    /// <summary>The player may still produce output (false once it ended or was stopped).</summary>
    bool IsSourceActive { get; }

    double? Aspect { get; }

    bool DebugOverlay { get; }

    /// <summary>Completes when the first frame of the stream was sent.</summary>
    Task FirstFrameSent { get; }

    void Pause();

    void Resume();

    void Seek(double seconds);

    void SetAspect(double? aspect);

    void SetDebugOverlay(bool on);

    /// <summary>Human-readable diagnostics snapshot (for <c>!media debug</c>).</summary>
    string DiagnosticsText();

    /// <summary>Structured diagnostics snapshot (soak harness), when the pipeline keeps one.</summary>
    Diagnostics.StreamDiagnosticsSnapshot? DiagnosticsSnapshot() => null;
}

public enum PipelineEndReason
{
    Natural,
    Stopped,
    Failed,

    /// <summary>The go-live connection dropped under a running stream (the item can be resumed at <see cref="PipelineEnd.Position"/>).</summary>
    ConnectionLost,
}

/// <summary>How a pipeline finished; <see cref="Error"/> is the user-facing message for failures.</summary>
public sealed record PipelineEnd(PipelineEndReason Reason, string? Error = null)
{
    /// <summary>Playback position when the stream ended, when known.</summary>
    public double? Position { get; init; }
}

/// <summary>Video player + audio sender for one item over one go-live link.</summary>
public interface IPipeline
{
    IPlayback Playback { get; }

    void Start();

    /// <summary>Completes (never faults) when the stream ends, is stopped or fails.</summary>
    Task<PipelineEnd> Completion { get; }

    /// <summary>Stop video and audio and wait for both (bounded). Idempotent.</summary>
    Task StopAsync();
}

public interface IPipelineFactory
{
    /// <summary>False when no H.264 encoder works (go-live impossible).</summary>
    bool EncoderAvailable { get; }

    IPipeline Create(StreamRequest request, IGoLiveLink link);
}

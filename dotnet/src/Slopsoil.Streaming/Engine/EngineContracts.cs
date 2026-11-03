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

    public bool? Live { get; init; } = true;

    public long ProbeSize { get; init; } = 2_000_000;

    public double StartOffset { get; init; }

    public double? Aspect { get; init; }

    public bool DebugOverlay { get; init; }
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
}

public enum PipelineEndReason
{
    Natural,
    Stopped,
    Failed,
}

/// <summary>How a pipeline finished; <see cref="Error"/> is the user-facing message for failures.</summary>
public sealed record PipelineEnd(PipelineEndReason Reason, string? Error = null);

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

namespace JellySend.Streaming.Video;

/// <summary>How the video player runs one item (the <c>H264VideoPlayer</c> constructor arguments).</summary>
public sealed record VideoPlayerOptions
{
    /// <summary>Input URL or path (may carry credentials: only ever logged redacted).</summary>
    public required string Url { get; init; }

    public required EncoderConfig Encoder { get; init; }

    /// <summary>Software encoder retried once when <see cref="Encoder"/> produces no frames at all.</summary>
    public EncoderConfig? Fallback { get; init; }

    /// <summary>Video SSRC; names the per-stream FIFO and overlay file.</summary>
    public uint VideoSsrc { get; init; }

    public double Fps { get; init; } = 30.0;

    public bool? Live { get; init; } = true;

    /// <summary>The source has an audio track (else FFmpeg generates silence into the FIFO).</summary>
    public bool HasAudio { get; init; } = true;

    /// <summary>Which of the input's own audio tracks to play (0 = first, FFmpeg <c>0:a:N</c>; spec 013); null = the first.</summary>
    public int? AudioTrack { get; init; }

    public long ProbeSize { get; init; } = 2_000_000;

    /// <summary>Media position to start at, seconds.</summary>
    public double StartOffset { get; init; }

    /// <summary>Forced display aspect ratio; null keeps the source's.</summary>
    public double? Aspect { get; init; }

    /// <summary>Burn the diagnostics overlay into the picture from the start.</summary>
    public bool DebugOverlay { get; init; }

    /// <summary>
    /// Read-ahead (<c>STREAM_BUFFER_SECONDS</c>, 0–30): access units buffered ahead of playout, and the pre-roll a
    /// segment waits for before its first frame. 0 = no read-ahead (start with the first frame).
    /// </summary>
    public double BufferSeconds { get; init; }

    /// <summary>Fraction (0–0.95) of the frame interval to spread a frame's packets over; 0 bursts them.</summary>
    public double PacketPace { get; init; }

    /// <summary>Output box <c>W:H</c>, for diagnostics and the overlay text size.</summary>
    public string Resolution { get; init; } = "1920:1080";

    /// <summary>Video bitrate, for diagnostics.</summary>
    public string Bitrate { get; init; } = "?";

    /// <summary>Directory for the audio FIFO and the overlay text file.</summary>
    public string TempDirectory { get; init; } = Path.GetTempPath();

    public string Executable { get; init; } = "ffmpeg";

    /// <summary>Runs on the player thread before each FFmpeg start (e.g. register the video SSRC with DAVE).</summary>
    public Action? OnSegmentStarting { get; init; }

    /// <summary>Loading card (spec 015): sent while the read-ahead fills; null until encoded or not configured.</summary>
    public Func<StillClip?>? LoadingStill { get; init; }

    /// <summary>Pause card (spec 015): sent while paused; null until encoded or not configured.</summary>
    public Func<StillClip?>? PausedStill { get; init; }
}

/// <summary>Why the player finished.</summary>
public enum VideoPlayerEnd
{
    /// <summary>The input ran out: the engine may auto-advance.</summary>
    Natural,

    /// <summary><see cref="VideoPlayer.Stop"/> was called.</summary>
    Stopped,

    /// <summary>The player thread failed (e.g. FFmpeg could not start).</summary>
    Failed,
}

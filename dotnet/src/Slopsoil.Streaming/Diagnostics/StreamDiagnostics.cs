using Slopsoil.Streaming.Contracts;

namespace Slopsoil.Streaming.Diagnostics;

/// <summary>Per-packet outcomes of the UDP send path (reported by <see cref="RtpMediaSender"/>).</summary>
public interface ITransportStats
{
    void PacketSent();

    /// <summary>The socket refused the datagram (returned false).</summary>
    void PacketDropped();

    /// <summary>Sending threw (socket gone, I/O error).</summary>
    void SendError();
}

/// <summary>Per-frame timing reported by the video pacing loop.</summary>
public interface IVideoStats
{
    /// <summary>
    /// A frame was sent; <paramref name="slack"/> is the time left until its pacing deadline
    /// (≤ 0 means it was late by <c>-slack</c>).
    /// </summary>
    void VideoFrameSent(TimeSpan slack);

    /// <summary>Time the loop spent blocked reading FFmpeg's stdout.</summary>
    void VideoReadStall(TimeSpan blocked);

    /// <summary>The encoder of the segment now running (changes on software fallback).</summary>
    void SetEncoder(string encoder);

    /// <summary>Video read ahead of playout (frames buffered / fps).</summary>
    void VideoBuffered(TimeSpan buffered);
}

/// <summary>
/// Immutable view of the last ≤ 10 s of the pipeline (data-model.md "StreamDiagnostics"), read by the overlay,
/// the periodic log line and <c>!media debug</c>. <see cref="Window"/>, <see cref="Frames"/> and
/// <see cref="LateTotalMs"/> are extra so the log line can match the Python "video stats" line field for field.
/// </summary>
public sealed record StreamDiagnosticsSnapshot
{
    public double FpsSent { get; init; }

    public double FpsTarget { get; init; }

    public int LateFrames { get; init; }

    public double MaxLatenessMs { get; init; }

    public double ReadStallMs { get; init; }

    public double PacketsPerSec { get; init; }

    public long SendDrops { get; init; }

    public long SendErrors { get; init; }

    /// <summary>Latest audio-minus-video send position on the shared playout clock; null until audio reports one.</summary>
    public double? DriftMs { get; init; }

    /// <summary>Video buffered ahead of playout (seconds).</summary>
    public double VideoBufferSeconds { get; init; }

    /// <summary>Audio buffered ahead of playout (seconds).</summary>
    public double AudioBufferSeconds { get; init; }

    public int SilenceSteps { get; init; }

    public int SkipSteps { get; init; }

    public string Encoder { get; init; } = "?";

    public string Resolution { get; init; } = "?";

    public string Bitrate { get; init; } = "?";

    public string? SourceCodec { get; init; }

    /// <summary>Playback position in seconds of the source media.</summary>
    public double Position { get; init; }

    /// <summary>Item runtime in seconds when known.</summary>
    public double? Runtime { get; init; }

    /// <summary>Length of the window the rates and counts cover.</summary>
    public TimeSpan Window { get; init; }

    /// <summary>Frames sent in the window.</summary>
    public int Frames { get; init; }

    /// <summary>Sum of lateness over the late frames in the window.</summary>
    public double LateTotalMs { get; init; }
}

/// <summary>
/// Rolling 10 s window of video, audio and transport counters in one-second buckets. Thread-safe: fed from the
/// video thread, the audio sender and the UDP send path.
/// </summary>
public sealed class StreamDiagnostics : ITransportStats, IVideoStats, IAudioStats
{
    public static readonly TimeSpan WindowLength = TimeSpan.FromSeconds(10);
    private const int Buckets = 10;

    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly long _start;
    private readonly Bucket[] _buckets = new Bucket[Buckets];
    private double? _driftMs;
    private TimeSpan _videoBuffered;
    private TimeSpan _audioBuffered;
    private string _encoder = "?";
    private string? _sourceCodec;
    private double? _runtime;

    public StreamDiagnostics(double fpsTarget, string resolution, string bitrate, TimeProvider? time = null)
    {
        FpsTarget = fpsTarget;
        Resolution = resolution;
        Bitrate = bitrate;
        _time = time ?? TimeProvider.System;
        _start = _time.GetTimestamp();
        for (var i = 0; i < Buckets; i++)
        {
            _buckets[i] = new Bucket { Second = -1 };
        }
    }

    public double FpsTarget { get; }

    public string Resolution { get; }

    public string Bitrate { get; }

    /// <summary>Source of <see cref="StreamDiagnosticsSnapshot.Position"/> (the video player).</summary>
    public IMediaClock? Clock { get; set; }

    public void SetEncoder(string encoder)
    {
        lock (_lock)
        {
            _encoder = encoder;
        }
    }

    /// <summary>Source codec and runtime of the item now playing (from PlaybackInfo / ffprobe).</summary>
    public void SetSource(string? codec, double? runtimeSeconds)
    {
        lock (_lock)
        {
            _sourceCodec = codec;
            _runtime = runtimeSeconds;
        }
    }

    public void VideoFrameSent(TimeSpan slack) => Update(b =>
    {
        b.Frames++;
        if (slack <= TimeSpan.Zero)
        {
            var late = -slack.TotalMilliseconds;
            b.Late++;
            b.LateTotalMs += late;
            b.MaxLateMs = Math.Max(b.MaxLateMs, late);
        }
    });

    public void VideoReadStall(TimeSpan blocked) => Update(b => b.ReadStallMs += blocked.TotalMilliseconds);

    public void PacketSent() => Update(b => b.Packets++);

    public void PacketDropped() => Update(b => b.Drops++);

    public void SendError() => Update(b => b.Errors++);

    public void AudioSilenceStep() => Update(b => b.Silence++);

    public void AudioSkipStep() => Update(b => b.Skip++);

    public void AudioDrift(double milliseconds)
    {
        lock (_lock)
        {
            _driftMs = milliseconds;
        }
    }

    public void VideoBuffered(TimeSpan buffered)
    {
        lock (_lock)
        {
            _videoBuffered = buffered;
        }
    }

    public void AudioBuffered(TimeSpan buffered)
    {
        lock (_lock)
        {
            _audioBuffered = buffered;
        }
    }

    public StreamDiagnosticsSnapshot Snapshot()
    {
        var position = Clock?.Position ?? 0.0;
        lock (_lock)
        {
            var elapsed = _time.GetElapsedTime(_start);
            var current = CurrentSecond(elapsed);
            var first = Math.Max(0, current - (Buckets - 1));
            var window = elapsed - TimeSpan.FromSeconds(first);
            var sum = new Bucket();
            foreach (var b in _buckets.Where(b => b.Second >= first && b.Second <= current))
            {
                sum.Add(b);
            }

            var seconds = window.TotalSeconds;
            return new StreamDiagnosticsSnapshot
            {
                FpsSent = seconds > 0 ? sum.Frames / seconds : 0,
                FpsTarget = FpsTarget,
                LateFrames = sum.Late,
                MaxLatenessMs = sum.MaxLateMs,
                ReadStallMs = sum.ReadStallMs,
                PacketsPerSec = seconds > 0 ? sum.Packets / seconds : 0,
                SendDrops = sum.Drops,
                SendErrors = sum.Errors,
                DriftMs = _driftMs,
                VideoBufferSeconds = _videoBuffered.TotalSeconds,
                AudioBufferSeconds = _audioBuffered.TotalSeconds,
                SilenceSteps = sum.Silence,
                SkipSteps = sum.Skip,
                Encoder = _encoder,
                Resolution = Resolution,
                Bitrate = Bitrate,
                SourceCodec = _sourceCodec,
                Position = position,
                Runtime = _runtime,
                Window = window,
                Frames = sum.Frames,
                LateTotalMs = sum.LateTotalMs,
            };
        }
    }

    private static long CurrentSecond(TimeSpan elapsed) => (long)Math.Floor(elapsed.TotalSeconds);

    private void Update(Action<Bucket> change)
    {
        lock (_lock)
        {
            var second = CurrentSecond(_time.GetElapsedTime(_start));
            var bucket = _buckets[second % Buckets];
            if (bucket.Second != second)
            {
                bucket.Reset(second);
            }

            change(bucket);
        }
    }

    private sealed class Bucket
    {
        public long Second;
        public int Frames;
        public int Late;
        public double LateTotalMs;
        public double MaxLateMs;
        public double ReadStallMs;
        public long Packets;
        public long Drops;
        public long Errors;
        public int Silence;
        public int Skip;

        public void Reset(long second)
        {
            Second = second;
            Frames = Late = Silence = Skip = 0;
            LateTotalMs = MaxLateMs = ReadStallMs = 0;
            Packets = Drops = Errors = 0;
        }

        public void Add(Bucket b)
        {
            Frames += b.Frames;
            Late += b.Late;
            LateTotalMs += b.LateTotalMs;
            MaxLateMs = Math.Max(MaxLateMs, b.MaxLateMs);
            ReadStallMs += b.ReadStallMs;
            Packets += b.Packets;
            Drops += b.Drops;
            Errors += b.Errors;
            Silence += b.Silence;
            Skip += b.Skip;
        }
    }
}

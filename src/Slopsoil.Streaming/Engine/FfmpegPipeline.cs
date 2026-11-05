using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Slopsoil.Common.Config;
using Slopsoil.Streaming.Audio;
using Slopsoil.Streaming.Diagnostics;
using Slopsoil.Streaming.Process;
using Slopsoil.Streaming.Video;

namespace Slopsoil.Streaming.Engine;

/// <summary>Builds the real FFmpeg pipeline: video player + RTP sender + diagnostics + audio sender.</summary>
public sealed class FfmpegPipelineFactory : IPipelineFactory
{
    private readonly SlopsoilOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly Lazy<EncoderSelection?> _encoders;

    public FfmpegPipelineFactory(SlopsoilOptions options, ILoggerFactory? loggers = null, Func<EncoderSelection?>? detect = null)
    {
        _options = options;
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _encoders = new Lazy<EncoderSelection?>(
            detect ?? (() => new EncoderDetector(SystemProcessRunner.Instance, _loggers.CreateLogger<EncoderDetector>()).Detect(options.Resolution, options.VideoBitrate)));
    }

    /// <summary>How long to wait for FFmpeg to open the audio FIFO (a cold server transcode can take a while).</summary>
    public TimeSpan FifoTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Directory for the per-stream FIFO and overlay file.</summary>
    public string TempDirectory { get; init; } = Path.GetTempPath();

    public bool EncoderAvailable => _encoders.Value is not null;

    /// <summary>Loading / pause cards (spec 015); null = none.</summary>
    public StillImages? Stills { get; init; }

    public IPipeline Create(StreamRequest request, IGoLiveLink link)
    {
        var encoders = _encoders.Value ?? throw new InvalidOperationException("no H.264 encoder available");
        return new FfmpegPipeline(request, link, encoders, _options, FifoTimeout, TempDirectory, _loggers, Stills);
    }
}

/// <summary>One item over one go-live link (<c>engine._run_live</c>).</summary>
internal sealed class FfmpegPipeline : IPipeline
{
    /// <summary>How long the audio may keep playing its buffer after the video reached the end of the input.</summary>
    private static readonly TimeSpan NaturalDrain = TimeSpan.FromSeconds(2);

    private readonly VideoPlayer _player;
    private readonly RtpMediaSender _sender;
    private readonly StreamDiagnostics _diagnostics;
    private readonly TimeSpan _fifoTimeout;
    private readonly SlopsoilOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<PipelineEnd> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _audioLock = new();
    private AudioSender? _audio;
    private StreamPcmSource? _pcm;
    private int _started;
    private string? _holdPath;

    public FfmpegPipeline(
        StreamRequest request, IGoLiveLink link, EncoderSelection encoders, SlopsoilOptions options, TimeSpan fifoTimeout, string tempDirectory, ILoggerFactory loggers,
        StillImages? stills = null)
    {
        _options = options;
        _fifoTimeout = fifoTimeout;
        _loggers = loggers;
        _log = loggers.CreateLogger<FfmpegPipeline>();
        // Spec 007: STREAM_FPS is a ceiling; the output follows the source rate (evenly divided when above it).
        var fps = FrameRate.Choose(request.SourceFps, options.Fps);
        // Spec 010: a runtime output height replaces the configured box and scales the bitrate with it.
        var (resolution, bitrate) = request.OutputHeight is { } height
            ? OutputSize.For(height, options.Resolution, options.VideoBitrate)
            : (options.Resolution, options.VideoBitrate);
        if (request.OutputHeight is not null)
        {
            encoders = new EncoderSelection(encoders.Primary.For(resolution, bitrate), encoders.Fallback?.For(resolution, bitrate));
        }

        _diagnostics = new StreamDiagnostics(fps, resolution, bitrate);
        _diagnostics.SetSource(
            request.SourceCodec,
            request.RuntimeSeconds,
            FrameRate.Normalize(request.SourceFps),
            request.JellyfinTranscodeReason is { } why ? StreamDiagnostics.JellyfinTranscode(why) : StreamDiagnostics.OriginalFile);
        _sender = new RtpMediaSender(link.SendPacket, link.Mode, link.SecretKey, link.AudioSsrc, link.VideoSsrc, () => link.Dave) { Stats = _diagnostics };
        var playerOptions = new VideoPlayerOptions
        {
            Url = request.Url,
            Encoder = encoders.Primary,
            Fallback = encoders.Fallback,
            VideoSsrc = link.VideoSsrc,
            Fps = fps,
            Live = request.Live,
            HasAudio = request.HasAudio,
            AudioTrack = request.AudioTrack,
            ProbeSize = request.ProbeSize,
            StartOffset = request.StartOffset,
            Aspect = request.Aspect,
            DebugOverlay = request.DebugOverlay,
            PacketPace = options.PacketPace,
            BufferSeconds = options.StreamBufferSeconds,
            Resolution = resolution,
            Bitrate = bitrate,
            TempDirectory = tempDirectory,
            OnSegmentStarting = () => link.Dave?.RegisterVideoSsrc(link.VideoSsrc),
            LoadingStill = stills?.Loading(encoders.Primary, fps),
            PausedStill = stills?.Paused(encoders.Primary, fps),
        };
        _player = new VideoPlayer(playerOptions, _sender, diagnostics: _diagnostics, logger: loggers.CreateLogger<VideoPlayer>());
        Playback = new PlayerPlayback(_player, _diagnostics);
    }

    public IPlayback Playback { get; }

    public Task<PipelineEnd> Completion => _end.Task;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _player.Start();

        // A second name for the FIFO, so a reader blocked in open() can always be released (OpenFifoAsync) even
        // after the player removed the original path (FFmpeg failed at once).
        _holdPath = _player.AudioFifoPath + ".hold";
        File.Delete(_holdPath);
        if (LibC.Link(_player.AudioFifoPath, _holdPath) != 0)
        {
            _log.LogWarning("could not link {Fifo} (errno {Errno}); a failed start may leave a blocked reader", _player.AudioFifoPath, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            _holdPath = null;
        }

        _ = Task.Run(RunAsync);
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _player.Stop();
        AudioSender? running;
        lock (_audioLock)
        {
            running = _audio; // RunAsync will not create one after _stop is cancelled
        }

        running?.Stop();
        if (running is { } audio)
        {
            await audio.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }

        // Close our read end before waiting for FFmpeg (Python: join audio, then fifo_file.close()): with nobody
        // draining, FFmpeg blocks writing the full FIFO and ignores SIGTERM until killed 3 s later.
        _pcm?.Dispose();
        await _player.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        RemoveHold();
        _end.TrySetResult(new PipelineEnd(PipelineEndReason.Stopped));
    }

    private async Task RunAsync()
    {
        try
        {
            _log.LogInformation("waiting for audio FIFO from FFmpeg (up to {Seconds:0} s)...", _fifoTimeout.TotalSeconds);
            _pcm = await OpenFifoAsync(_player.AudioFifoPath).ConfigureAwait(false);
            RemoveHold();
            if (_pcm is null)
            {
                if (!_stop.IsCancellationRequested)
                {
                    _log.LogError("go-live: no audio from FFmpeg (it exited or did not open the audio FIFO in time)");
                    _player.Stop();
                    _end.TrySetResult(new PipelineEnd(PipelineEndReason.Failed, "go-live stream failed to start"));
                }

                return;
            }

            lock (_audioLock)
            {
                if (_stop.IsCancellationRequested)
                {
                    // Stopped while the FIFO was opening: StopAsync handles the rest.
                    _pcm.Dispose();
                    return;
                }

                _audio = new AudioSender(
                    _pcm, _player, _diagnostics, _sender, _options.AvSyncMs, _loggers.CreateLogger<AudioSender>(), bufferSeconds: _options.StreamBufferSeconds);
                _audio.Start();
            }

            var end = await _player.Completion.ConfigureAwait(false);
            if (end == VideoPlayerEnd.Natural)
            {
                // Let the audio play out what it has buffered up to the end of the input, as the video did.
                await _audio.Completion.WaitAsync(NaturalDrain).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            }

            _audio.Stop();
            await _audio.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            _pcm.Dispose();
            _end.TrySetResult(end switch
            {
                VideoPlayerEnd.Natural => new PipelineEnd(PipelineEndReason.Natural),
                VideoPlayerEnd.Stopped => new PipelineEnd(PipelineEndReason.Stopped),
                _ => new PipelineEnd(PipelineEndReason.Failed, "go-live error: the video encoder failed — see the logs"),
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "go-live pipeline error");
            _player.Stop();
            _end.TrySetResult(new PipelineEnd(PipelineEndReason.Failed, $"go-live error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Open the FIFO's read end (blocks until FFmpeg opens the write end). On timeout or stop the blocked open is
    /// released by opening the write end ourselves, so no thread is left stuck; returns null then.
    /// </summary>
    private async Task<StreamPcmSource?> OpenFifoAsync(string path)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var open = Task.Run(() => StreamPcmSource.OpenFifo(path));
        // Give up early when the player already finished (FFmpeg failed before opening its outputs).
        var winner = await Task.WhenAny(open, Task.Delay(_fifoTimeout, _stop.Token), _player.Completion).ConfigureAwait(false);
        if (winner == open || open.IsCompletedSuccessfully)
        {
            // Opened (possibly at the same time as a very short clip already finished): use it.
            var pcm = await open.ConfigureAwait(false);
            _log.LogInformation("audio FIFO opened after {Elapsed:0.00}s", waited.Elapsed.TotalSeconds);
            return pcm;
        }

        if (_player.Completion.IsCompleted)
        {
            _log.LogWarning(
                "FFmpeg exited ({End}) before opening the audio FIFO after {Elapsed:0.0}s, {Frames} video frames read — see the ffmpeg lines above",
                _player.Completion.Result, waited.Elapsed.TotalSeconds, _player.SegmentFrames);
        }
        else if (!_stop.IsCancellationRequested)
        {
            _log.LogWarning(
                "FFmpeg is still running but did not open the audio FIFO within {Timeout:0} s ({Frames} video frames read so far)",
                _fifoTimeout.TotalSeconds, _player.SegmentFrames);
        }

        try
        {
            using var unblock = new FileStream(_holdPath ?? path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0, FileOptions.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Both names gone: nothing we can do; the blocked open is abandoned.
            _log.LogWarning("could not release the audio FIFO reader: {Error}", ex.Message);
            return null;
        }

        try
        {
            (await open.ConfigureAwait(false)).Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private void RemoveHold()
    {
        if (Interlocked.Exchange(ref _holdPath, null) is { } hold)
        {
            try
            {
                File.Delete(hold);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The player as the commands see it.</summary>
    private sealed class PlayerPlayback(VideoPlayer player, StreamDiagnostics diagnostics) : IPlayback
    {
        public double Position => player.Position;

        public bool IsPaused => player.IsPaused;

        public bool IsSourceActive => player.IsSourceActive;

        public double? Aspect => player.Aspect;

        public bool DebugOverlay => player.DebugOverlay;

        public Task FirstFrameSent => player.FirstFrameSent;

        public void Pause() => player.PausePlayback();

        public void Resume() => player.ResumePlayback();

        public void Seek(double seconds) => player.Seek(seconds);

        public void SetAspect(double? aspect) => player.SetAspect(aspect);

        public void SetDebugOverlay(bool on) => player.SetDebugOverlay(on);

        public string DiagnosticsText() => DiagnosticsFormat.Overlay(diagnostics.Snapshot());

        public StreamDiagnosticsSnapshot? DiagnosticsSnapshot() => diagnostics.Snapshot();
    }
}

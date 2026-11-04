using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Slopsoil.Common.Logging;
using Slopsoil.Streaming.Contracts;
using Slopsoil.Streaming.Diagnostics;
using Slopsoil.Streaming.Process;

namespace Slopsoil.Streaming.Video;

/// <summary>
/// Reads H.264 Annex-B from an FFmpeg child on a reader thread, groups it into access units into a bounded
/// read-ahead buffer (<see cref="VideoPlayerOptions.BufferSeconds"/>), and on the player thread sends them paced
/// to the segment's playout clock at exactly <see cref="Fps"/> once the pre-roll is buffered (spec 004). The clock's
/// origin is published as <see cref="Anchor"/> so the audio sender schedules on the same clock; FFmpeg's PCM audio
/// goes to <see cref="AudioFifoPath"/>. Seek, aspect and overlay changes restart FFmpeg in place (with <c>-ss</c>)
/// so viewers stay in the screenshare; a hardware encoder that produces nothing is retried once in software.
/// Port of <c>video_player.H264VideoPlayer</c>.
/// </summary>
public sealed class VideoPlayer : IMediaClock, IDisposable
{
    /// <summary>How long a restart waits for the audio sender to drain the old FIFO audio.</summary>
    public static readonly TimeSpan DrainWait = TimeSpan.FromMilliseconds(500);

    /// <summary>Grace period after SIGTERM before SIGKILL.</summary>
    public static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long a naturally ended FFmpeg may take to exit before it is killed.</summary>
    public static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    /// <summary>Overlay text refresh period.</summary>
    public static readonly TimeSpan OverlayRefresh = TimeSpan.FromSeconds(1);

    /// <summary>Startup window in which known decoder noise on stderr is demoted to debug.</summary>
    public static readonly TimeSpan StartupNoiseWindow = TimeSpan.FromSeconds(10);

    private const int ReadSize = 65_536;

    // Transient libopenh264 errors on P-frames before the first IDR; harmless within the first GOP.
    private static readonly string[] StartupNoise =
        ["DecodeFrame failed", "no exist Sequence Parameter Sets", "Error submitting packet to decoder"];

    private readonly VideoPlayerOptions _options;
    private readonly IVideoFrameSink _sink;
    private readonly IProcessRunner _processes;
    private readonly IFifoFactory _fifos;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly object _ctl = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<VideoPlayerEnd> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _timestampIncrement;
    private readonly PacketPacer? _pacer;
    private readonly int _prerollFrames;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _stopRequested;
    private volatile bool _restarting;
    private volatile bool _heldByPause;
    private volatile EncoderConfig _encoder;
    private volatile IMediaProcess? _process;
    private volatile FollowerHandle? _follower;
    private CancellationTokenSource? _segmentCts;
    private double? _seekTo;
    private bool _finished;
    private double? _aspect;
    private bool _debug;
    private double _offset;
    private int _segmentFrames;
    private int _restartSeq;
    private DiagnosticsLogger? _statsLogger;
    private RepeatingTimer? _overlayTimer;
    private PlayoutAnchor? _anchor;
    private long _pausedAt;
    private volatile FrameBuffer<IReadOnlyList<byte[]>>? _frames;
    private volatile Exception? _readerError;

    // Pacing state of the current packet (player thread only).
    private long _frameStart;

    public VideoPlayer(
        VideoPlayerOptions options,
        IVideoFrameSink sink,
        IProcessRunner? processes = null,
        IFifoFactory? fifos = null,
        StreamDiagnostics? diagnostics = null,
        TimeProvider? time = null,
        ILogger? logger = null)
    {
        _options = options;
        _sink = sink;
        _processes = processes ?? SystemProcessRunner.Instance;
        _fifos = fifos ?? UnixFifo.Instance;
        _time = time ?? TimeProvider.System;
        _log = logger ?? NullLogger.Instance;
        _encoder = options.Encoder;
        _offset = Math.Max(0.0, options.StartOffset);
        _aspect = options.Aspect;
        _debug = options.DebugOverlay;
        Fps = options.Fps;
        _timestampIncrement = (int)Math.Round(RtpMediaSender.VideoClock / options.Fps);
        _pacer = options.PacketPace > 0 ? PacePacket : null;
        _prerollFrames = (int)Math.Round(Math.Clamp(options.BufferSeconds, 0.0, 30.0) * options.Fps);
        BufferCapacity = Math.Max(1, _prerollFrames);
        Diagnostics = diagnostics ?? new StreamDiagnostics(options.Fps, options.Resolution, options.Bitrate, _time);
        Diagnostics.Clock ??= this;
        AudioFifoPath = Path.Combine(options.TempDirectory, string.Create(CultureInfo.InvariantCulture, $"slopsoil_{options.VideoSsrc}_audio.fifo"));
        OverlayPath = Path.Combine(options.TempDirectory, string.Create(CultureInfo.InvariantCulture, $"slopsoil_{options.VideoSsrc}_debug.txt"));
    }

    public double Fps { get; }

    /// <summary>Most access units read ahead of playout (≈ buffer seconds × fps; 1 without read-ahead).</summary>
    public int BufferCapacity { get; }

    /// <summary>Access units buffered ahead of playout right now.</summary>
    public int BufferedFrames => _frames?.Count ?? 0;

    public PlayoutAnchor? Anchor => Volatile.Read(ref _anchor);

    /// <summary>FIFO FFmpeg writes s16le 48 kHz stereo PCM into; exists from <see cref="Start"/> until the player ends.</summary>
    public string AudioFifoPath { get; }

    /// <summary>The source has real audio (otherwise the FIFO carries generated silence).</summary>
    public bool HasAudio => _options.HasAudio;

    /// <summary>The text file the debug overlay is drawn from.</summary>
    public string OverlayPath { get; }

    public StreamDiagnostics Diagnostics { get; }

    /// <summary>Encoder of the current segment (switches to the fallback after a no-output failure).</summary>
    public EncoderConfig Encoder => _encoder;

    public PauseGate Pause { get; } = new();

    public bool IsPaused => !Pause.IsOpen;

    public bool StopRequested => _stopRequested;

    /// <summary>Completes when the first video frame has been sent (audio should not start before it).</summary>
    public Task FirstFrameSent => _firstFrame.Task;

    /// <summary>Completes when the player thread has exited and cleaned up.</summary>
    public Task<VideoPlayerEnd> Completion => _completion.Task;

    public double? Aspect
    {
        get
        {
            lock (_ctl)
            {
                return _aspect;
            }
        }
    }

    public bool DebugOverlay
    {
        get
        {
            lock (_ctl)
            {
                return _debug;
            }
        }
    }

    /// <summary>Current position in source seconds; a pending seek reports its target.</summary>
    public double Position
    {
        get
        {
            lock (_ctl)
            {
                if (_seekTo is { } target)
                {
                    return target;
                }
            }

            return SegmentOffset + (SegmentFrames / Fps);
        }
    }

    public int RestartSeq => Volatile.Read(ref _restartSeq);

    public double SegmentOffset => Volatile.Read(ref _offset);

    public int SegmentFrames => Volatile.Read(ref _segmentFrames);

    public bool SeekPending
    {
        get
        {
            lock (_ctl)
            {
                return _seekTo is not null || _restarting;
            }
        }
    }

    public bool IsSourceActive => _running && !_stopRequested;

    /// <summary>The player thread is holding a frame at the closed pause gate (test observation point).</summary>
    internal bool HeldByPause => _heldByPause;

    /// <summary>Create the FIFO and start the player thread.</summary>
    public void Start()
    {
        lock (_ctl)
        {
            if (_stopRequested && _thread is null)
            {
                return; // stopped before it started: Completion is already set
            }

            if (_thread is not null || _finished)
            {
                throw new InvalidOperationException("the video player was already started");
            }

            _fifos.Create(AudioFifoPath);
            _running = true;
            _statsLogger = new DiagnosticsLogger(Diagnostics, _time, _log);
            UpdateOverlayTimer();
            _thread = new Thread(Run) { IsBackground = true, Name = "H264VideoPlayer" };
        }

        _thread.Start();
    }

    /// <summary>Stop playback (idempotent): FFmpeg's stdout is closed, then it gets SIGTERM.</summary>
    public void Stop()
    {
        IMediaProcess? process;
        lock (_ctl)
        {
            if (_stopRequested)
            {
                return;
            }

            _stopRequested = true;
            process = _process;
            _segmentCts?.Cancel();
            if (_thread is null)
            {
                _finished = true;
                _completion.TrySetResult(VideoPlayerEnd.Stopped);
            }
        }

        _stopCts.Cancel();
        if (process is not null)
        {
            // SIGTERM only. Closing stdout here would block on .NET/Linux until the player thread's in-flight Read
            // returns (a silent FFmpeg — cold transcode, stalled input — never returns), so no signal would be sent.
            // The player thread closes stdout itself in KillProcess, where it cannot race its own Read. If FFmpeg
            // ignores SIGTERM (blocked in a syscall), escalate to SIGKILL after the usual grace.
            Signal(process, closeStdout: false);
            _ = Task.Run(async () =>
            {
                try
                {
                    // On the player's clock (fake in tests); only observes the process, never waits on it.
                    await Task.Delay(TerminateGrace, _time).ConfigureAwait(false);
                    if (!process.HasExited)
                    {
                        _log.LogWarning("FFmpeg ignored SIGTERM for {Grace}; killing it", TerminateGrace);
                        process.Kill();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
                {
                    // Already gone or reaped by the player thread.
                }
            });
        }
    }

    public void Dispose() => Stop();

    /// <summary>Close the pause gate; both streams hold at their next slot.</summary>
    public void PausePlayback()
    {
        lock (_ctl)
        {
            if (Pause.IsOpen)
            {
                _pausedAt = _time.GetTimestamp();
                Pause.Close();
            }
        }
    }

    /// <summary>Shift the playout origin by the pause (FR-004: for video and audio alike), then open the gate.</summary>
    public void ResumePlayback()
    {
        lock (_ctl)
        {
            if (Pause.IsOpen)
            {
                return;
            }

            if (_anchor is { } anchor)
            {
                Volatile.Write(ref _anchor, anchor with { Origin = anchor.Origin + (_time.GetTimestamp() - _pausedAt) });
            }

            Pause.Open();
        }
    }

    /// <summary>Restart decoding from <paramref name="seconds"/> without tearing down the stream.</summary>
    public void Seek(double seconds)
    {
        IMediaProcess? process;
        lock (_ctl)
        {
            if (_finished)
            {
                return; // already ended: nothing left to restart
            }

            _seekTo = Math.Max(0.0, seconds);
            process = _process;
            _segmentCts?.Cancel();
        }

        if (process is not null)
        {
            Signal(process, closeStdout: false);
        }
    }

    /// <summary>Change the forced aspect ratio (null = source); restarts FFmpeg in place at the current position.</summary>
    public void SetAspect(double? aspect)
    {
        lock (_ctl)
        {
            _aspect = aspect;
        }

        Seek(Position);
    }

    /// <summary>Turn the burned-in diagnostics overlay on or off via the in-place restart path.</summary>
    public void SetDebugOverlay(bool on)
    {
        lock (_ctl)
        {
            if (_debug == on || _finished)
            {
                return;
            }

            _debug = on;
            UpdateOverlayTimer();
        }

        Seek(Position);
    }

    public IAudioFollowerHandle RegisterAudioFollower()
    {
        var handle = new FollowerHandle(this);
        _follower = handle;
        return handle;
    }

    /// <summary>SIGTERM then SIGKILL if needed; stdout is closed first so a child blocked on a full pipe exits.</summary>
    internal void KillProcess(IMediaProcess process)
    {
        if (process.HasExited)
        {
            return;
        }

        // FFmpeg ignores SIGTERM while blocked writing to a full pipe; closing our end gives it EPIPE.
        try
        {
            process.CloseStandardOutput();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        process.Terminate();
        if (!process.WaitForExit(TerminateGrace))
        {
            _log.LogWarning("FFmpeg did not exit after SIGTERM — sending SIGKILL");
            process.Kill();
            process.WaitForExit(Timeout.InfiniteTimeSpan);
        }
    }

    private static void Signal(IMediaProcess process, bool closeStdout)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (closeStdout)
            {
                process.CloseStandardOutput();
            }

            process.Terminate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException)
        {
            // raced with the player thread reaping it
        }
    }

    private void Run()
    {
        var end = VideoPlayerEnd.Natural;
        try
        {
            while (!_stopRequested)
            {
                StreamSegment();
                if (_stopRequested)
                {
                    break;
                }

                if (ApplyPendingSeek())
                {
                    continue;
                }

                // A hardware encoder that produced no frames at all (e.g. VA-API "Access unit too large" on big
                // MPEG-2 keyframes): retry once in software so the stream still plays.
                if (SegmentFrames == 0 && _options.Fallback is { } fallback && !ReferenceEquals(_encoder, fallback))
                {
                    _log.LogWarning(
                        "video encoder {Encoder} produced no output — retrying with software encoder {Fallback}", _encoder.Name, fallback.Name);
                    _encoder = fallback;
                    ReleaseProcess();
                    Volatile.Write(ref _segmentFrames, 0);
                    SignalRestart();
                    continue;
                }

                // Natural end — unless a seek slipped in since the check above.
                bool finished;
                lock (_ctl)
                {
                    if (_seekTo is null)
                    {
                        _finished = true;
                    }

                    finished = _finished;
                }

                if (finished)
                {
                    break;
                }

                ApplyPendingSeek();
            }
        }
        catch (Exception ex)
        {
            _log.LogError("H264VideoPlayer error: {Error}", Redactor.RedactText(ex.ToString()));
            end = VideoPlayerEnd.Failed;
        }
        finally
        {
            Finish(end);
        }
    }

    private void Finish(VideoPlayerEnd end)
    {
        lock (_ctl)
        {
            _finished = true;
            _seekTo = null;
            Volatile.Write(ref _anchor, null);
            _overlayTimer?.Dispose();
            _overlayTimer = null;
        }

        try
        {
            if (_process is { } process)
            {
                KillProcess(process);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("could not reap FFmpeg: {Error}", ex.Message);
        }

        ReleaseProcess();
        _fifos.Remove(AudioFifoPath);
        OverlayFile.Delete(OverlayPath);
        _statsLogger?.Dispose();
        _running = false;
        _log.LogInformation("H264VideoPlayer stopped");
        _completion.TrySetResult(_stopRequested ? VideoPlayerEnd.Stopped : end);
    }

    private void ReleaseProcess()
    {
        var process = _process;
        _process = null;
        process?.Dispose();
    }

    /// <summary>Consume a pending seek: reap FFmpeg and reset the segment for the next attempt.</summary>
    private bool ApplyPendingSeek()
    {
        double? target;
        lock (_ctl)
        {
            target = _seekTo;
            _seekTo = null;
            _restarting = target is not null;
        }

        if (target is not { } seekTo)
        {
            return false;
        }

        try
        {
            _log.LogInformation("seeking to {Position:F1}s", seekTo);
            if (_process is { } process)
            {
                KillProcess(process);
            }

            ReleaseProcess();
            Volatile.Write(ref _segmentFrames, 0);
            Volatile.Write(ref _offset, seekTo);
            SignalRestart();
        }
        finally
        {
            lock (_ctl)
            {
                _restarting = false;
            }
        }

        return true;
    }

    /// <summary>Bump <see cref="RestartSeq"/> and give the audio sender ≤ 0.5 s to drain the old FIFO audio.</summary>
    private void SignalRestart()
    {
        var follower = _follower;
        follower?.Reset();
        Interlocked.Increment(ref _restartSeq);
        if (follower is not null && !_stopRequested)
        {
            follower.WaitDrained(DrainWait, _time, _stopCts.Token);
        }
    }

    private void Unregister(FollowerHandle handle) => Interlocked.CompareExchange(ref _follower, null, handle);

    private CancellationToken BeginSegment()
    {
        lock (_ctl)
        {
            _segmentCts?.Dispose();
            _segmentCts = new CancellationTokenSource();
            if (_stopRequested || _seekTo is not null)
            {
                _segmentCts.Cancel();
            }

            return _segmentCts.Token;
        }
    }

    private bool Interrupted => _stopRequested || SeekPendingTarget;

    private bool SeekPendingTarget
    {
        get
        {
            lock (_ctl)
            {
                return _seekTo is not null;
            }
        }
    }

    private FfmpegCommandOptions CommandOptions()
    {
        bool debug;
        double? aspect;
        lock (_ctl)
        {
            debug = _debug;
            aspect = _aspect;
        }

        if (debug)
        {
            // drawtext fails to start without its file: write it before FFmpeg does.
            OverlayFile.Write(OverlayPath, DiagnosticsFormat.Overlay(Diagnostics.Snapshot()));
        }

        return new FfmpegCommandOptions
        {
            Url = _options.Url,
            Encoder = _encoder,
            AudioFifo = AudioFifoPath,
            Fps = Fps,
            Live = _options.Live,
            HasAudio = _options.HasAudio,
            ProbeSize = _options.ProbeSize,
            Offset = SegmentOffset,
            Aspect = aspect,
            OverlayFile = debug ? OverlayPath : null,
            OverlayFontSize = OverlayFontSize(_options.Resolution),
            Executable = _options.Executable,
        };
    }

    internal static int OverlayFontSize(string resolution)
    {
        var parts = resolution.Split(':');
        return parts.Length == 2 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var h) && h > 0
            ? Math.Max(12, h / 40)
            : 24;
    }

    private void StreamSegment()
    {
        try
        {
            _options.OnSegmentStarting?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogWarning("segment start hook failed: {Error}", ex.Message);
        }

        var token = BeginSegment();
        var args = FfmpegCommand.Build(CommandOptions());
        var url = Redactor.RedactUrl(_options.Url);
        _log.LogInformation("Starting H.264 video stream from {Url}", url);
        _log.LogInformation(
            "FFmpeg encoder={Encoder} command: {Command}",
            _encoder.Name,
            string.Join(' ', args.Select(a => a == _options.Url ? url : Redactor.RedactText(a))));

        var process = _processes.Start(args);
        _process = process;
        // stop() or seek() while FFmpeg was starting: don't wait for its first frame.
        if (Interrupted)
        {
            KillProcess(process);
            return;
        }

        StartStderrDrain(process);
        Diagnostics.SetEncoder(_encoder.Name);
        Volatile.Write(ref _segmentFrames, 0);
        lock (_ctl)
        {
            Volatile.Write(ref _anchor, null);
        }

        var frames = new FrameBuffer<IReadOnlyList<byte[]>>(BufferCapacity);
        _frames = frames;
        var reader = new Thread(() => ReadLoop(process, frames, token)) { IsBackground = true, Name = "H264VideoReader" };
        reader.Start();
        try
        {
            PlayLoop(frames, token);
        }
        finally
        {
            if (frames.IsCompleted)
            {
                reader.Join();
            }
            else
            {
                StopReader(process, reader);
            }

            _frames = null;
            Diagnostics.VideoBuffered(TimeSpan.Zero);
        }

        if (Interlocked.Exchange(ref _readerError, null) is { } error)
        {
            ExceptionDispatchInfo.Throw(error);
        }

        if (Interrupted && !process.HasExited)
        {
            KillProcess(process);
        }

        if (!process.WaitForExit(ExitGrace))
        {
            _log.LogWarning("FFmpeg still running after stream end — killing");
            process.Kill();
            process.WaitForExit(Timeout.InfiniteTimeSpan);
        }

        _log.LogInformation("H.264 video stream ended (exit code {Code})", process.ExitCode);
    }

    /// <summary>
    /// Reader thread: stdout → access units → the bounded buffer, eagerly (FR-002). Blocks while the buffer is full,
    /// so FFmpeg is held back by its pipe. Never touches the time provider's timers.
    /// </summary>
    private void ReadLoop(IMediaProcess process, FrameBuffer<IReadOnlyList<byte[]>> frames, CancellationToken token)
    {
        try
        {
            var splitter = new AccessUnitSplitter();
            var buffer = new byte[ReadSize];
            while (!_stopRequested && !token.IsCancellationRequested)
            {
                var readStart = _time.GetTimestamp();
                int read;
                try
                {
                    read = process.StandardOutput.Read(buffer);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    read = 0; // stdout closed by stop()
                }

                Diagnostics.VideoReadStall(_time.GetElapsedTime(readStart));
                if (read == 0)
                {
                    break;
                }

                if (!AddAll(frames, splitter.Feed(buffer.AsSpan(0, read)), token))
                {
                    return;
                }
            }

            // Flush the tail only on a natural end: after stop/seek it is stale.
            if (!Interrupted && !token.IsCancellationRequested)
            {
                AddAll(frames, splitter.Flush(), token);
            }
        }
        catch (Exception ex)
        {
            _readerError = ex;
        }
        finally
        {
            frames.Complete();
        }
    }

    private static bool AddAll(FrameBuffer<IReadOnlyList<byte[]>> frames, IReadOnlyList<IReadOnlyList<byte[]>> units, CancellationToken token)
    {
        foreach (var unit in units)
        {
            if (unit.Count > 0 && !frames.Add(unit, token))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// End a reader that is still running (stop/seek, or the player failed): SIGTERM lets a blocked Read return;
    /// SIGKILL if FFmpeg ignores it. Only then may stdout be closed (closing it under a Read blocks on .NET/Linux).
    /// </summary>
    private void StopReader(IMediaProcess process, Thread reader)
    {
        if (reader.Join(TimeSpan.Zero))
        {
            return;
        }

        Signal(process, closeStdout: false);
        if (reader.Join(TerminateGrace))
        {
            return;
        }

        _log.LogWarning("FFmpeg did not exit after SIGTERM — sending SIGKILL");
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
        }

        reader.Join(TerminateGrace);
    }

    /// <summary>Player thread: wait for the pre-roll (FR-003), then send every buffered unit on the playout clock.</summary>
    private void PlayLoop(FrameBuffer<IReadOnlyList<byte[]>> frames, CancellationToken token)
    {
        if (_prerollFrames > 0 && !frames.WaitFor(_prerollFrames, token))
        {
            return;
        }

        while (!_stopRequested && frames.Take(token) is { } frame)
        {
            Diagnostics.VideoBuffered(TimeSpan.FromSeconds(frames.Count / Fps));
            if (Emit(frame, token))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Send frame <c>n</c> of the segment at <c>origin + n / fps</c> and wait for the next slot. The first frame
    /// publishes the origin (<see cref="Anchor"/>); a pause holds the frame and shifts the origin (in
    /// <see cref="ResumePlayback"/>); a late frame (underrun) goes at once and the schedule stays absolute.
    /// True → stop playing this segment.
    /// </summary>
    private bool Emit(IReadOnlyList<byte[]> frame, CancellationToken token)
    {
        var n = SegmentFrames;
        long origin;
        while (true)
        {
            if (!Pause.IsOpen)
            {
                _heldByPause = true;
                var opened = Pause.WaitOpen(Timeout.InfiniteTimeSpan, token);
                _heldByPause = false;
                if (!opened)
                {
                    return true; // stop or seek while paused
                }
            }

            if (Interrupted)
            {
                return true;
            }

            if (Anchor is not { } anchor)
            {
                lock (_ctl)
                {
                    if (!Pause.IsOpen)
                    {
                        continue; // paused just now: hold the first frame
                    }

                    anchor = new PlayoutAnchor(RestartSeq, _time.GetTimestamp());
                    Volatile.Write(ref _anchor, anchor);
                }
            }

            origin = anchor.Origin;
            var wait = Ticks(origin, n) - _time.GetTimestamp();
            if (wait <= MinWaitTicks)
            {
                if (Pause.IsOpen)
                {
                    break;
                }

                continue;
            }

            // Ahead of the slot (only after a pause moved the origin): wait for it, then look again.
            if (Sleep(TimeSpan.FromSeconds((double)wait / _time.TimestampFrequency), token))
            {
                return true;
            }
        }

        _frameStart = _time.GetTimestamp();
        _sink.SendVideoFrame(frame, _timestampIncrement, _pacer);
        _firstFrame.TrySetResult();
        Volatile.Write(ref _segmentFrames, n + 1);

        var due = Ticks(Anchor?.Origin ?? origin, n + 1);
        var slack = TimeSpan.FromSeconds((double)(due - _time.GetTimestamp()) / _time.TimestampFrequency);
        Diagnostics.VideoFrameSent(slack);
        if (slack > TimeSpan.FromMilliseconds(1))
        {
            return Sleep(slack, token);
        }

        return _stopRequested;
    }

    private long MinWaitTicks => _time.TimestampFrequency / 1000;

    private long Ticks(long origin, int frame) => origin + (long)(frame / Fps * _time.TimestampFrequency);

    /// <summary>Spread a frame's packets over <c>PacketPace</c> of the frame interval (absolute deadlines).</summary>
    private bool PacePacket(int index, int count)
    {
        if (count <= 1)
        {
            return true;
        }

        var perPacket = _options.PacketPace / Fps / count;
        var deadline = _frameStart + (long)((index + 1) * perPacket * _time.TimestampFrequency);
        var slack = deadline - _time.GetTimestamp();
        CancellationToken token;
        lock (_ctl)
        {
            token = _segmentCts?.Token ?? CancellationToken.None;
        }

        return slack <= 0 || !Sleep(TimeSpan.FromSeconds((double)slack / _time.TimestampFrequency), token);
    }

    /// <summary>Sleep on the player thread; true if interrupted (stop or seek).</summary>
    private bool Sleep(TimeSpan duration, CancellationToken token)
    {
        if (ReferenceEquals(_time, TimeProvider.System))
        {
            // A wait handle is not at the mercy of a busy thread pool, unlike timer callbacks.
            return token.WaitHandle.WaitOne(duration);
        }

        try
        {
            Task.Delay(duration, _time, token).Wait(CancellationToken.None);
            return false;
        }
        catch (AggregateException) when (token.IsCancellationRequested)
        {
            return true;
        }
    }

    private void StartStderrDrain(IMediaProcess process)
    {
        var started = _time.GetTimestamp();
        var thread = new Thread(() =>
        {
            try
            {
                while (process.StandardError.ReadLine() is { } line)
                {
                    var message = Redactor.RedactText(line.TrimEnd());
                    if (message.Length == 0)
                    {
                        continue;
                    }

                    if (_time.GetElapsedTime(started) < StartupNoiseWindow && StartupNoise.Any(n => message.Contains(n, StringComparison.Ordinal)))
                    {
                        _log.LogDebug("ffmpeg (startup): {Line}", message);
                    }
                    else
                    {
                        _log.LogWarning("ffmpeg video: {Line}", message);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        })
        { IsBackground = true, Name = "ffmpeg-stderr" };
        thread.Start();
    }

    /// <summary>Under <see cref="_ctl"/>: run the overlay refresh only while debug is on and the player runs.</summary>
    private void UpdateOverlayTimer()
    {
        if (_debug && _running && _overlayTimer is null)
        {
            _overlayTimer = new RepeatingTimer(_time, OverlayRefresh, WriteOverlay, ex => _log.LogDebug("overlay write failed: {Error}", ex.Message));
        }
        else if (!_debug && _overlayTimer is not null)
        {
            _overlayTimer.Dispose();
            _overlayTimer = null;
        }
    }

    private void WriteOverlay()
    {
        var text = DiagnosticsFormat.Overlay(Diagnostics.Snapshot());
        // A tick still running when the player finished (its timer is disposed without waiting) must not recreate
        // the overlay file Finish just deleted.
        if (Volatile.Read(ref _finished) || !Volatile.Read(ref _debug))
        {
            return;
        }

        OverlayFile.Write(OverlayPath, text);
    }

    private sealed class FollowerHandle(VideoPlayer owner) : IAudioFollowerHandle
    {
        private readonly object _lock = new();
        private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SignalDrained()
        {
            lock (_lock)
            {
                _drained.TrySetResult();
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                if (_drained.Task.IsCompleted)
                {
                    _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        public void WaitDrained(TimeSpan timeout, TimeProvider time, CancellationToken ct)
        {
            Task task;
            lock (_lock)
            {
                task = _drained.Task;
            }

            try
            {
                task.WaitAsync(timeout, time, ct).Wait(CancellationToken.None);
            }
            catch (AggregateException)
            {
                // timed out or stopping: start the next FFmpeg anyway
            }
        }

        public void Dispose()
        {
            owner.Unregister(this);
            SignalDrained();
        }
    }
}

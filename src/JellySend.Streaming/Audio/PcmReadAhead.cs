using Microsoft.Extensions.Logging;
using JellySend.Discord.Native;

namespace JellySend.Streaming.Audio;

/// <summary>
/// The audio read-ahead (spec 004 FR-002): a reader thread pulls whole 20 ms PCM frames out of FFmpeg's FIFO into a
/// bounded queue as fast as FFmpeg writes them, so the FIFO never fills and a slow transcode only drains the queue.
/// A short read means the current writer closed the FIFO (end of a segment): the reader is then "in the gap" and
/// polls until a new writer appears or the source is no longer active. Waits are real (never a fake clock).
/// </summary>
internal sealed class PcmReadAhead
{
    private const int FrameBytes = OpusEncoder.FrameBytes;
    private static readonly TimeSpan GapPoll = TimeSpan.FromMilliseconds(10);

    private readonly IPcmSource _pcm;
    private readonly Func<bool> _sourceActive;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private readonly Queue<byte[]> _frames = new();
    private readonly Stack<byte[]> _free = new();
    private readonly ManualResetEventSlim _stop = new();
    private Thread? _thread;
    private bool _inGap;
    private bool _blockedFull;
    private bool _ended;
    private bool _discarding;
    private Action? _onDrained;
    private int _discarded;

    /// <param name="pcm">FFmpeg's PCM output.</param>
    /// <param name="capacity">Most frames buffered; the reader stops reading (FFmpeg blocks) while full.</param>
    /// <param name="sourceActive">False once no new FIFO writer can appear: the reader ends at the next gap.</param>
    /// <param name="log">Errors of the reader thread.</param>
    public PcmReadAhead(IPcmSource pcm, int capacity, Func<bool> sourceActive, ILogger log)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _pcm = pcm;
        Capacity = capacity;
        _sourceActive = sourceActive;
        _log = log;
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>The current writer closed the FIFO and no new data has arrived since.</summary>
    public bool InGap
    {
        get
        {
            lock (_lock)
            {
                return _inGap;
            }
        }
    }

    /// <summary>The reader has exited: nothing more will be buffered.</summary>
    public bool Ended
    {
        get
        {
            lock (_lock)
            {
                return _ended;
            }
        }
    }

    /// <summary>The reader is not about to buffer more by itself (ended, in a gap or full): a test observation point.</summary>
    public bool Settled
    {
        get
        {
            lock (_lock)
            {
                return _ended || _inGap || _blockedFull;
            }
        }
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "GoLiveAudioReader" };
        _thread.Start();
    }

    /// <summary>Copy the oldest buffered frame into <paramref name="frame"/>; false if none is buffered.</summary>
    public bool TryTake(Span<byte> frame)
    {
        lock (_lock)
        {
            if (!_frames.TryDequeue(out var buffered))
            {
                return false;
            }

            buffered.CopyTo(frame);
            _free.Push(buffered);
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>
    /// Discard the current writer's audio (FR-007): everything buffered now, and everything read until its end of
    /// stream. <paramref name="onDrained"/> runs once the old writer's audio is gone (at once when already in the
    /// gap), so the player may start the next FFmpeg without its audio mixing with the old one's.
    /// </summary>
    /// <returns>Frames dropped from the buffer.</returns>
    public int BeginDrain(Action? onDrained)
    {
        int cleared;
        var drainedNow = false;
        lock (_lock)
        {
            cleared = _frames.Count;
            while (_frames.TryDequeue(out var f))
            {
                _free.Push(f);
            }

            if (_inGap || _ended)
            {
                drainedNow = true;
            }
            else
            {
                _discarding = true;
                _onDrained = onDrained is null ? _onDrained : _onDrained + onDrained;
            }

            Monitor.PulseAll(_lock);
        }

        if (drainedNow)
        {
            onDrained?.Invoke();
        }

        return cleared;
    }

    /// <summary>Frames read and thrown away by drains so far.</summary>
    public int Discarded
    {
        get
        {
            lock (_lock)
            {
                return _discarded;
            }
        }
    }

    /// <summary>Ask the reader to exit; it does after its current read returns.</summary>
    public void Stop()
    {
        _stop.Set();
        lock (_lock)
        {
            Monitor.PulseAll(_lock);
        }
    }

    public bool Join(TimeSpan timeout) => _thread?.Join(timeout) ?? true;

    private bool Stopping => _stop.IsSet;

    private void Run()
    {
        try
        {
            var scratch = new byte[FrameBytes];
            while (!Stopping)
            {
                int n;
                try
                {
                    n = _pcm.ReadFrame(scratch);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    return; // FIFO closed by the pipeline
                }

                if (n == FrameBytes)
                {
                    Store(scratch);
                    continue;
                }

                // Short read: the writer closed its end (a partial frame is dropped, as before).
                Action? drained;
                lock (_lock)
                {
                    _inGap = true;
                    drained = _onDrained;
                    _onDrained = null;
                    _discarding = false;
                }

                drained?.Invoke();
                if (!_sourceActive())
                {
                    return;
                }

                _stop.Wait(GapPoll);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GoLiveAudio: PCM reader failed");
        }
        finally
        {
            lock (_lock)
            {
                _ended = true;
                Monitor.PulseAll(_lock);
            }

            Action? drained;
            lock (_lock)
            {
                drained = _onDrained;
                _onDrained = null;
            }

            drained?.Invoke();
        }
    }

    private void Store(byte[] scratch)
    {
        lock (_lock)
        {
            _inGap = false;
            while (_frames.Count >= Capacity && !_discarding && !Stopping)
            {
                _blockedFull = true;
                Monitor.Wait(_lock);
            }

            _blockedFull = false;
            if (_discarding)
            {
                _discarded++;
                return;
            }

            if (Stopping)
            {
                return;
            }

            var frame = _free.Count > 0 ? _free.Pop() : new byte[FrameBytes];
            scratch.CopyTo(frame, 0);
            _frames.Enqueue(frame);
        }
    }
}

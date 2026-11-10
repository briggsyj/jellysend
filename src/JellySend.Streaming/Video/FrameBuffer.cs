namespace JellySend.Streaming.Video;

/// <summary>
/// The bounded read-ahead between a segment's stdout reader and its playout loop (spec 004 FR-002): the reader
/// blocks while it is full (so FFmpeg blocks on its pipe), the player blocks while it is empty. Thread-safe;
/// one instance per segment. Waits are on real monitors, never on a <see cref="TimeProvider"/>.
/// </summary>
internal sealed class FrameBuffer<T>
    where T : class
{
    private readonly object _lock = new();
    private readonly Queue<T> _items = new();
    private bool _completed;

    public FrameBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>The producer finished (end of input, stop or error): nothing more will be added.</summary>
    public bool IsCompleted
    {
        get
        {
            lock (_lock)
            {
                return _completed;
            }
        }
    }

    /// <summary>Append, waiting while full. False (item dropped) when cancelled or completed.</summary>
    public bool Add(T item, CancellationToken token)
    {
        using var wake = token.Register(PulseAll);
        lock (_lock)
        {
            while (_items.Count >= Capacity && !_completed && !token.IsCancellationRequested)
            {
                Monitor.Wait(_lock);
            }

            if (_completed || token.IsCancellationRequested)
            {
                return false;
            }

            _items.Enqueue(item);
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>Take the oldest item, waiting while empty. Null when completed and drained, or cancelled.</summary>
    public T? Take(CancellationToken token)
    {
        using var wake = token.Register(PulseAll);
        lock (_lock)
        {
            while (_items.Count == 0 && !_completed && !token.IsCancellationRequested)
            {
                Monitor.Wait(_lock);
            }

            if (token.IsCancellationRequested || _items.Count == 0)
            {
                return null;
            }

            var item = _items.Dequeue();
            Monitor.PulseAll(_lock);
            return item;
        }
    }

    /// <summary>Wait until <paramref name="count"/> items are buffered or the producer completed. False if cancelled.</summary>
    public bool WaitFor(int count, CancellationToken token)
    {
        var target = Math.Min(count, Capacity);
        using var wake = token.Register(PulseAll);
        lock (_lock)
        {
            while (_items.Count < target && !_completed && !token.IsCancellationRequested)
            {
                Monitor.Wait(_lock);
            }

            return !token.IsCancellationRequested;
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            Monitor.PulseAll(_lock);
        }
    }

    private void PulseAll()
    {
        lock (_lock)
        {
            Monitor.PulseAll(_lock);
        }
    }
}

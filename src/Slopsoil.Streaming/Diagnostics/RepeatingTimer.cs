namespace Slopsoil.Streaming.Diagnostics;

/// <summary>
/// Runs <c>tick</c> every <c>period</c> on a <see cref="TimeProvider"/> timer, re-armed after each tick so ticks
/// never overlap. Exceptions from <c>tick</c> go to <c>onError</c> and do not stop the timer.
/// </summary>
internal sealed class RepeatingTimer : IDisposable
{
    private readonly TimeSpan _period;
    private readonly Action _tick;
    private readonly Action<Exception> _onError;
    private readonly ITimer _timer;
    private volatile bool _disposed;

    public RepeatingTimer(TimeProvider time, TimeSpan period, Action tick, Action<Exception> onError)
    {
        _period = period;
        _tick = tick;
        _onError = onError;
        _timer = time.CreateTimer(_ => Fire(), null, period, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Stop the timer. Never waits for a tick in progress: callers dispose while holding their own locks, and a tick
    /// may need those locks (overlay tick → player position), so waiting here could deadlock. A tick already running
    /// finishes on its own and is not re-armed.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    private void Fire()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _tick();
        }
        catch (Exception ex)
        {
            _onError(ex);
        }

        // Re-armed only after the tick, so ticks never overlap.
        if (!_disposed)
        {
            try
            {
                _timer.Change(_period, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

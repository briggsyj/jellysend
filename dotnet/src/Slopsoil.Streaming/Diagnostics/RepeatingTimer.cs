namespace Slopsoil.Streaming.Diagnostics;

/// <summary>
/// Runs <c>tick</c> every <c>period</c> on a <see cref="TimeProvider"/> timer, re-armed after each tick so ticks
/// never overlap. Exceptions from <c>tick</c> go to <c>onError</c> and do not stop the timer.
/// </summary>
internal sealed class RepeatingTimer : IDisposable
{
    private readonly object _lock = new();
    private readonly TimeSpan _period;
    private readonly Action _tick;
    private readonly Action<Exception> _onError;
    private readonly ITimer _timer;
    private bool _disposed;

    public RepeatingTimer(TimeProvider time, TimeSpan period, Action tick, Action<Exception> onError)
    {
        _period = period;
        _tick = tick;
        _onError = onError;
        _timer = time.CreateTimer(_ => Fire(), null, period, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }

    private void Fire()
    {
        lock (_lock)
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

            _timer.Change(_period, Timeout.InfiniteTimeSpan);
        }
    }
}

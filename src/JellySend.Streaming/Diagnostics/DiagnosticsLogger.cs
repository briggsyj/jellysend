using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellySend.Streaming.Diagnostics;

/// <summary>
/// Logs <see cref="DiagnosticsFormat.LogLine"/> every 10 s while a stream runs, debug overlay on or off
/// (FR-053, the Python "video stats" line). Dispose to stop; disposing logs a final line for the partial window.
/// </summary>
public sealed class DiagnosticsLogger : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly StreamDiagnostics _diagnostics;
    private readonly ILogger _log;
    private readonly RepeatingTimer _timer;
    private int _disposed;

    public DiagnosticsLogger(StreamDiagnostics diagnostics, TimeProvider? time = null, ILogger? logger = null)
    {
        _diagnostics = diagnostics;
        _log = logger ?? NullLogger.Instance;
        _timer = new RepeatingTimer(time ?? TimeProvider.System, Interval, LogNow, ex => _log.LogWarning(ex, "video stats failed"));
    }

    public void LogNow() => _log.LogInformation("{Stats}", DiagnosticsFormat.LogLine(_diagnostics.Snapshot()));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
            LogNow();
        }
    }
}

using Microsoft.Extensions.Logging;

namespace JellySend.Common.Logging;

/// <summary>
/// Wraps another provider so every formatted message and exception is passed through
/// <see cref="Redactor.RedactText"/> (the <c>RedactingFormatter</c> catch-all).
/// </summary>
public sealed class RedactingLoggerProvider(ILoggerProvider inner) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void Dispose() => inner.Dispose();

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = Redactor.RedactText(formatter(state, exception));
            var redacted = exception is null ? null : new RedactedException(exception);
            inner.Log(logLevel, eventId, message, redacted, static (m, _) => m);
        }
    }
}

/// <summary>An exception stand-in whose message and stack text are redacted.</summary>
public sealed class RedactedException(Exception original)
    : Exception(Redactor.RedactText($"{original.GetType().FullName}: {original.Message}"))
{
    public override string? StackTrace => Redactor.RedactText(original.StackTrace);

    public override string ToString() => Redactor.RedactText(original.ToString());
}

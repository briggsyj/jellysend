using Microsoft.Extensions.Logging;
using JellySend.Common.Logging;

namespace JellySend.Tests.Common;

public sealed class RedactorTests
{
    // Expected values produced by redact.py.
    [Theory]
    [InlineData("http://user:pw@Jelly.local:8096/Videos/1/master.m3u8?ApiKey=abc&api_key=def&MediaSourceId=9", "http://jelly.local:8096/Videos/1/master.m3u8?MediaSourceId=9")]
    [InlineData("https://h/x?token=t", "https://h/x")]
    [InlineData("https://h/p?a=1&b=2", "https://h/p?a=1&b=2")]
    [InlineData("https://h/p?X-Emby-Token=z&Password=q&access_token=a", "https://h/p")]
    public void Redact_url_matches_python(string url, string expected) => Assert.Equal(expected, Redactor.RedactUrl(url));

    [Fact]
    public void Redact_url_of_non_url_falls_back_to_text() =>
        Assert.Equal("not a url ?api_key=***", Redactor.RedactUrl("not a url ?api_key=k"));

    [Theory]
    [InlineData("/tmp/clip.mp4?api_key=SECRET&x=1", "/tmp/clip.mp4?api_key=***&x=1")]
    [InlineData("C:\\media\\a.mkv?token=SECRET", "C:\\media\\a.mkv?token=***")]
    public void Redact_url_of_a_path_never_leaks(string path, string expected) =>
        Assert.Equal(expected, Redactor.RedactUrl(path));

    [Theory]
    [InlineData("Error opening http://h/x?api_key=SECRET&y=1 failed", "Error opening http://h/x?api_key=***&y=1 failed")]
    [InlineData("rtsp://admin:hunter2@cam/stream", "rtsp://***@cam/stream")]
    [InlineData("a;password=p\"q", "a;password=***\"q")]
    [InlineData("&APIKEY=x ", "&APIKEY=*** ")]
    [InlineData("no secrets here", "no secrets here")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Redact_text_matches_python(string? text, string expected) => Assert.Equal(expected, Redactor.RedactText(text));

    [Fact]
    public void Logger_provider_redacts_messages_and_exceptions()
    {
        var sink = new SinkProvider();
        using var provider = new RedactingLoggerProvider(sink);
        var logger = provider.CreateLogger("test");
        Assert.True(logger.IsEnabled(LogLevel.Information));
        using (logger.BeginScope("scope"))
        {
            logger.LogWarning(new InvalidOperationException("GET http://h/?api_key=k1 failed"), "playing {Url}", "http://u:p@h/x?token=t2");
        }

        var (message, exception) = Assert.Single(sink.Entries);
        Assert.Equal("playing http://***@h/x?token=***", message);
        Assert.NotNull(exception);
        Assert.DoesNotContain("k1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("k1", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal("", exception.StackTrace ?? "");
        Assert.True(sink.Disposed is false);
    }

    [Fact]
    public void Redacted_exception_stack_trace_is_redacted()
    {
        try
        {
            throw new InvalidOperationException("x");
        }
        catch (InvalidOperationException ex)
        {
            var redacted = new RedactedException(ex);
            Assert.Equal(Redactor.RedactText(ex.StackTrace), redacted.StackTrace);
        }

        var sink = new SinkProvider();
        new RedactingLoggerProvider(sink).Dispose();
        Assert.True(sink.Disposed);
    }

    private sealed class SinkProvider : ILoggerProvider, ILogger
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];

        public bool Disposed { get; private set; }

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose() => Disposed = true;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception), exception));
    }
}

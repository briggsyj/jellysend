using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Slopsoil.Services.Media;

namespace Slopsoil.Tests.Services;

/// <summary>One request the fake handler saw.</summary>
internal sealed record SeenRequest(HttpMethod Method, string Url, string? Authorization, string? Body)
{
    public JsonElement Json => JsonDocument.Parse(Body!).RootElement;

    public string Path => new Uri(Url).AbsolutePath;

    public Dictionary<string, string> Query => new Uri(Url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
}

/// <summary>Records requests and answers with a scripted responder (default: 200 "{}").</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    public List<SeenRequest> Requests { get; } = [];

    public Func<SeenRequest, HttpResponseMessage> Responder { get; set; } = _ => Json("{}");

    /// <summary>When set, used instead of <see cref="Responder"/> (e.g. to hang until cancelled).</summary>
    public Func<SeenRequest, CancellationToken, Task<HttpResponseMessage>>? AsyncResponder { get; set; }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Json(object body) => Json(JsonSerializer.Serialize(body));

    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var auth = request.Headers.TryGetValues("Authorization", out var v) ? v.Single() : null;
        var seen = new SeenRequest(request.Method, request.RequestUri!.ToString(), auth, body);
        lock (Requests)
        {
            Requests.Add(seen);
        }

        ct.ThrowIfCancellationRequested();
        return AsyncResponder is { } async ? await async(seen, ct) : Responder(seen);
    }
}

/// <summary>Captures log entries (level + formatted message).</summary>
internal sealed class ListLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public string Text => string.Join('\n', Entries.Select(e => e.Message));

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    public bool Has(LogLevel level, string text) => Entries.Any(e => e.Level == level && e.Message.Contains(text, StringComparison.Ordinal));
}

/// <summary>Records ffprobe invocations and returns a scripted result.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout)> Calls { get; } = [];

    public Func<ProcessResult> Result { get; set; } = () => new ProcessResult(0, "", "");

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((fileName, arguments, timeout));
        return Task.FromResult(Result());
    }
}

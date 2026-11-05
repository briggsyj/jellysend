using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Slopsoil.Common.Logging;

namespace Slopsoil.Streaming.Engine;

/// <summary>
/// One go-live stream per guild (<c>streaming/engine.py</c> <c>start_live_stream</c> / <c>cancel_live_stream</c>):
/// replaces the old stream only after its cleanup (STREAM_DELETE) finished, connects go-live, runs the pipeline
/// and tears down only what it still owns. <c>onEnd</c> runs when the stream ended without a stop — naturally or
/// because the pipeline failed (Python's <c>ended_naturally = not stop_requested</c>) — with the reason.
/// </summary>
public sealed class StreamEngine(IGoLiveConnector connector, IPipelineFactory pipelines, ILogger<StreamEngine>? logger = null)
{
    /// <summary>How long a replacement waits for the old stream's cleanup.</summary>
    public static readonly TimeSpan OldStreamCleanupWait = TimeSpan.FromSeconds(5);

    private readonly ILogger _log = logger ?? NullLogger<StreamEngine>.Instance;
    private readonly ConcurrentDictionary<ulong, Session> _sessions = new();

    /// <summary>The guild's running stream controls, or null.</summary>
    public IPlayback? GetPlayback(ulong guildId) => _sessions.TryGetValue(guildId, out var s) ? s.Pipeline?.Playback : null;

    /// <summary>True while a stream is starting or running in the guild.</summary>
    public bool IsStreaming(ulong guildId) => _sessions.ContainsKey(guildId);

    /// <summary>
    /// Start <paramref name="request"/> as the guild's go-live stream. Returns true once the pipeline runs; false
    /// (after telling the user why via <paramref name="send"/>) if go-live could not start.
    /// </summary>
    public async Task<bool> StartAsync(
        ulong guildId, StreamRequest request, Func<string, Task> send, Func<PipelineEnd, Task>? onEnd = null, CancellationToken ct = default)
    {
        // Replies never throw out of the engine: a failed send must not leave a session registered.
        Task Say(string text) => SafeAsync(() => send(text), "reply");

        if (!pipelines.EncoderAvailable)
        {
            _log.LogWarning("no H.264 encoder available — go-live requires video encoding");
            await Say("❌ We can't broadcast — no video encoder works on this server. Ask an admin to check the logs.").ConfigureAwait(false);
            return false;
        }

        var session = new Session();
        if (_sessions.TryGetValue(guildId, out var old))
        {
            await StopSessionAsync(guildId, old, "replaced").ConfigureAwait(false);
            await old.Done.WaitAsync(OldStreamCleanupWait, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }

        _sessions[guildId] = session;
        _log.LogInformation("starting go-live stream: {Title} → {Url}", request.Title, Redactor.RedactUrl(request.Url));
        await Say($"📺 Now showing: **{request.Title}**{(request.TitleNote is { } note ? $" ({note})" : "")}").ConfigureAwait(false);

        IGoLiveLink link;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancel.Token);
            link = await connector.ConnectAsync(guildId, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Stopping || ct.IsCancellationRequested)
        {
            Release(guildId, session);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to establish go-live connection");
            Release(guildId, session);
            await Say("❌ We couldn't open the screenshare. Try again in a moment — if it keeps happening, ask an admin to check the logs.").ConfigureAwait(false);
            return false;
        }

        session.Link = link;
        if (session.Stopping)
        {
            // Stopped while connecting.
            await link.DisconnectAsync().ConfigureAwait(false);
            Release(guildId, session);
            return false;
        }

        IPipeline pipeline;
        try
        {
            pipeline = pipelines.Create(request, link);
            session.Pipeline = pipeline;
            pipeline.Start();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "go-live pipeline failed to start for {Title}", request.Title);
            await CleanupAsync(guildId, session).ConfigureAwait(false);
            await Say("❌ Something went wrong getting on air. Try again — if it keeps happening, ask an admin to check the logs.").ConfigureAwait(false);
            return false;
        }

        if (session.Stopping)
        {
            // A stop raced the pipeline creation (it saw Pipeline == null and left the cleanup to us).
            await CleanupAsync(guildId, session).ConfigureAwait(false);
            return false;
        }

        _log.LogInformation("go-live started for {Title}", request.Title);
        _ = Task.Run(() => RunAsync(guildId, session, request.Title, send, onEnd), CancellationToken.None);
        return true;
    }

    /// <summary>Stop the guild's stream (the go-live ends; voice stays). Waits for its cleanup.</summary>
    public async Task StopAsync(ulong guildId, string reason = "stop")
    {
        if (_sessions.TryGetValue(guildId, out var session))
        {
            await StopSessionAsync(guildId, session, reason).ConfigureAwait(false);
            await session.Done.WaitAsync(OldStreamCleanupWait, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stop the guild's stream only if it is still the one whose controls are <paramref name="playback"/> — an
    /// abandoned start must not end a newer stream that registered meanwhile.
    /// </summary>
    public async Task StopIfCurrentAsync(ulong guildId, IPlayback playback, string reason)
    {
        if (_sessions.TryGetValue(guildId, out var session) && ReferenceEquals(session.Pipeline?.Playback, playback))
        {
            await StopSessionAsync(guildId, session, reason).ConfigureAwait(false);
            await session.Done.WaitAsync(OldStreamCleanupWait, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    /// <summary>Shown when the screenshare connection drops under a running stream (the media layer may then resume).</summary>
    public const string ConnectionLostMessage = "📴 We've lost the signal — the screenshare dropped. `!play` to start again.";

    private async Task RunAsync(ulong guildId, Session session, string title, Func<string, Task> send, Func<PipelineEnd, Task>? onEnd)
    {
        var pipeline = session.Pipeline!;
        PipelineEnd end;
        if (await Task.WhenAny(pipeline.Completion, session.Link!.Closed).ConfigureAwait(false) == pipeline.Completion || session.Stopping)
        {
            end = await pipeline.Completion.ConfigureAwait(false);
        }
        else
        {
            // FFmpeg would keep sending UDP into a dead session: end it, report where it was.
            var position = pipeline.Playback.Position;
            _log.LogWarning("go-live connection for {Title} closed under the stream at {Position:0.0}s", title, position);
            await SafeAsync(pipeline.StopAsync, "stopping pipeline").ConfigureAwait(false);
            end = new PipelineEnd(PipelineEndReason.ConnectionLost, ConnectionLostMessage) { Position = position };
        }

        end = end with { FirstFrameSent = pipeline.Playback.FirstFrameSent.IsCompletedSuccessfully, Position = end.Position ?? pipeline.Playback.Position };
        var closeCode = session.Link!.Closed.IsCompleted ? session.Link.CloseCode : null;
        _log.LogInformation(
            "go-live stream {Title} ended: {Reason}{Error} at {Position:0.0}s, first frame {FirstFrame}, screenshare link {Link}",
            title, end.Reason, end.Error is null ? "" : $" ({end.Error})", end.Position, end.FirstFrameSent ? "sent" : "never sent",
            session.Link.Closed.IsCompleted ? $"closed by Discord ({closeCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no code"})" : "open");
        var unstopped = end.Reason != PipelineEndReason.Stopped && !session.Stopping;
        await CleanupAsync(guildId, session).ConfigureAwait(false);
        // With an end callback the media layer words the ending (spec 020: one message — a recovery, or a clear failure).
        if (end.Reason is PipelineEndReason.Failed or PipelineEndReason.ConnectionLost && end.Error is not null && unstopped && onEnd is null)
        {
            await SafeAsync(() => send(end.Error), "error reply").ConfigureAwait(false);
        }

        if (unstopped && onEnd is not null)
        {
            await SafeAsync(() => onEnd(end), $"end-of-stream callback for '{title}'").ConfigureAwait(false);
        }
    }

    /// <summary>Guilds with a stream starting or running (for shutdown).</summary>
    public IReadOnlyCollection<ulong> ActiveGuilds => [.. _sessions.Keys];

    private async Task StopSessionAsync(ulong guildId, Session session, string reason)
    {
        if (session.Stopping)
        {
            return;
        }

        session.Stopping = true;
        await session.Cancel.CancelAsync().ConfigureAwait(false);
        _log.LogInformation("stopping go-live in guild {Guild} ({Reason})", guildId, reason);
        if (session.Pipeline is null)
        {
            // Still connecting: StartAsync notices Stopping and cleans up.
            return;
        }

        await CleanupAsync(guildId, session).ConfigureAwait(false);
    }

    /// <summary>Stop pipeline, disconnect go-live, release the registry slot if still ours. Runs once.</summary>
    private async Task CleanupAsync(ulong guildId, Session session)
    {
        if (Interlocked.Exchange(ref session.CleanedUp, 1) != 0)
        {
            await session.Done.ConfigureAwait(false);
            return;
        }

        try
        {
            if (session.Pipeline is { } pipeline)
            {
                await SafeAsync(pipeline.StopAsync, "stopping pipeline").ConfigureAwait(false);
            }

            if (session.Link is { } link)
            {
                await SafeAsync(link.DisconnectAsync, "disconnecting go-live").ConfigureAwait(false);
            }
        }
        finally
        {
            Release(guildId, session);
        }
    }

    private void Release(ulong guildId, Session session)
    {
        // Owner check: a newer stream may already have registered for the guild.
        _sessions.TryRemove(new KeyValuePair<ulong, Session>(guildId, session));
        session.Finish();
    }

    private async Task SafeAsync(Func<Task> action, string what)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "error {What}", what);
        }
    }

    private sealed class Session
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CleanedUp;

        public volatile bool Stopping;

        /// <summary>Cancels a go-live connect still in progress when the session is stopped.</summary>
        public CancellationTokenSource Cancel { get; } = new();

        public IGoLiveLink? Link { get; set; }

        public IPipeline? Pipeline { get; set; }

        public Task Done => _done.Task;

        public void Finish() => _done.TrySetResult();
    }
}

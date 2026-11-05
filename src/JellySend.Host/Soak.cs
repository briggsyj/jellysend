using System.Diagnostics;
using Microsoft.Extensions.Logging;
using JellySend.Common.Config;
using JellySend.Discord.Dave;
using JellySend.Streaming.Diagnostics;
using JellySend.Streaming.Engine;

namespace JellySend.Host;

/// <summary>
/// T049 soak mode (<c>JELLYSEND_SOAK=1</c>): stream <c>JELLYSEND_SOAK_FILE</c> through the real pipeline with the
/// debug overlay on — no Discord, packets are encrypted and discarded — and append a diagnostics row to
/// <c>JELLYSEND_SOAK_CSV</c> every 10 s. See tests/soak/README.md.
/// </summary>
internal static class Soak
{
    public static async Task<int> RunAsync(JellySendOptions options, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger("JellySend.Soak");
        var file = Environment.GetEnvironmentVariable("JELLYSEND_SOAK_FILE");
        if (string.IsNullOrWhiteSpace(file))
        {
            log.LogCritical("JELLYSEND_SOAK_FILE is not set");
            return 1;
        }

        var csvPath = Environment.GetEnvironmentVariable("JELLYSEND_SOAK_CSV") is { Length: > 0 } c ? c : "jellysend-soak.csv";
        var factory = new FfmpegPipelineFactory(options, loggers);
        if (!factory.EncoderAvailable)
        {
            log.LogCritical("no working H.264 encoder");
            return 1;
        }

        var pipeline = factory.Create(new StreamRequest { Title = "soak", Url = file, Live = false, DebugOverlay = true }, new DiscardLink());
        await using var csv = new StreamWriter(csvPath, append: false) { AutoFlush = true };
        await csv.WriteLineAsync(SoakCsv.Header.AsMemory(), ct);
        var clock = Stopwatch.StartNew();
        pipeline.Start();
        log.LogInformation("soak: streaming {File} with the debug overlay; writing {Csv}", Common.Logging.Redactor.RedactUrl(file), csvPath);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var end = pipeline.Completion;
        try
        {
            while (!end.IsCompleted)
            {
                var tick = timer.WaitForNextTickAsync(ct).AsTask();
                if (await Task.WhenAny(tick, end) == tick && pipeline.Playback.DiagnosticsSnapshot() is { } snapshot)
                {
                    await csv.WriteLineAsync(SoakCsv.Row(clock.Elapsed.TotalSeconds, snapshot).AsMemory(), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            await pipeline.StopAsync();
        }

        var result = await pipeline.Completion;
        log.LogInformation("soak: finished after {Elapsed} ({Reason})", clock.Elapsed, result.Reason);
        return result.Reason == PipelineEndReason.Failed ? 1 : 0;
    }

    /// <summary>Go-live stand-in: the full packet path runs (DAVE off, transport crypto on), packets are dropped.</summary>
    private sealed class DiscardLink : IGoLiveLink
    {
        public string Mode => "aead_xchacha20_poly1305_rtpsize";

        public byte[] SecretKey { get; } = new byte[32];

        public uint AudioSsrc => 1000;

        public uint VideoSsrc => 1001;

        public IDaveSession? Dave => null;

        public bool SendPacket(ReadOnlySpan<byte> datagram) => true;

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

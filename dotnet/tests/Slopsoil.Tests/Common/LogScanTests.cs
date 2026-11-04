using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Slopsoil.Commands.Media;
using Slopsoil.Commands.Routing;
using Slopsoil.Commands.Voice;
using Slopsoil.Common.Config;
using Slopsoil.Common.Logging;
using Slopsoil.Streaming.Engine;
using Slopsoil.Tests.Commands;
using Slopsoil.Tests.Streaming;

namespace Slopsoil.Tests.Common;

/// <summary>
/// SC-006 (T042): a full play / pause / seek / playlist / stop session through the router, the real stream engine
/// and the real FFmpeg pipeline, with the secret in the stream URL — no credential may reach the raw logs.
/// </summary>
public sealed class LogScanTests : IDisposable
{
    private const string Secret = "SECRETKEY123";
    private readonly string _dir = Directory.CreateTempSubdirectory("slopsoil-logscan-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task No_credential_appears_in_logs_for_a_full_session()
    {
        Assert.SkipUnless(HasFfmpeg(), "ffmpeg not installed");

        // A real file whose name carries the secret exactly where a Jellyfin URL does.
        var clip = Path.Combine(_dir, $"clip.mp4?api_key={Secret}&MediaSourceId=1");
        MakeClip(clip, 30);

        var logs = new CapturingLoggerFactory();
        var options = SlopsoilOptions.From(new Dictionary<string, string?> { ["STREAM_RESOLUTION"] = "320:240", ["STREAM_FPS"] = "30", ["STREAM_VIDEO_BITRATE"] = "300k" });
        var connector = new FakeConnector();
        var engine = new StreamEngine(connector, new FfmpegPipelineFactory(options, logs) { TempDirectory = _dir }, logs.CreateLogger<StreamEngine>());
        var gateway = new FakeVoiceGateway();
        gateway.States[(5, 20)] = 50;
        var voice = new VoiceManager(gateway, logs.CreateLogger<VoiceManager>());
        var library = new FakeLibrary { UrlFor = _ => clip };
        library.Search["film"] = [MediaHarness.Movie("f", "Film", 2000)];
        var media = new MediaModule(library, engine, voice, logger: logs.CreateLogger<MediaModule>());
        var sender = new FakeSender();
        var router = new CommandRouter(new FakeRoles(), sender, new MessageHub(), (c, ex) => logs.CreateLogger("router").LogError(ex, "command {C} failed", c));
        media.Register(router, new VoiceModule(voice, media, logs.CreateLogger<VoiceModule>()));

        Task Send(string text) => router.DispatchAsync(new ChatMessage(5, 7, 20, text), TestContext.Current.CancellationToken);

        await Send("!media film");
        var playback = engine.GetPlayback(5);
        Assert.NotNull(playback);
        await playback.FirstFrameSent.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await Send("!media pause");
        await Send("!media resume 0:00:10");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        await Send("!media debug on");
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        await Send("!media playlist");
        await Send("!media debug");
        await Send("!media leave");

        var text = logs.Text;
        Assert.Contains("api_key=***", text, StringComparison.Ordinal); // the URL was logged — redacted
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, string.Join('\n', sender.Texts), StringComparison.Ordinal);
        Assert.Contains("⏹ stopped and left the voice channel", sender.Texts);
    }

    private static bool HasFfmpeg()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -version") { RedirectStandardOutput = true })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void MakeClip(string path, int seconds)
    {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=size=320x240:rate=30:duration={seconds}",
                     "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={seconds}", "-c:v", "libx264", "-c:a", "aac", "-shortest", "-f", "mp4", path])
        {
            info.ArgumentList.Add(a);
        }

        using var p = Process.Start(info)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    /// <summary>Raw (unredacted) capture of every category: the code itself must redact.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory, ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public string Text
        {
            get
            {
                lock (_lines)
                {
                    return string.Join('\n', _lines);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerFactory owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._lines)
                {
                    owner._lines.Add($"{category} {logLevel}: {formatter(state, exception)} {exception}");
                }
            }
        }
    }
}

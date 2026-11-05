using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Slopsoil.Common.Config;
using Slopsoil.Commands.Media;
using Slopsoil.Commands.Routing;
using Slopsoil.Commands.Voice;
using Slopsoil.Discord.Dave;
using Slopsoil.Discord.Gateway;
using Slopsoil.Discord.Native;
using Slopsoil.Discord.Rest;
using Slopsoil.Discord.Transport;
using Slopsoil.Discord.Voice;
using Slopsoil.Services.Jellyfin;
using Slopsoil.Services.Media;
using Slopsoil.Streaming.Engine;
using Slopsoil.Streaming.Video;

namespace Slopsoil.Host;

/// <summary>
/// The bot process (<c>bot.py</c>): log in over the gateway, keep the role directory current, and run
/// <c>!</c> commands from MESSAGE_CREATE, replying over REST. Wiring only; the parts are unit-tested.
/// </summary>
internal sealed class BotService(SlopsoilOptions options, ILoggerFactory loggers, IHostApplicationLifetime lifetime) : BackgroundService
{
    private readonly ILogger _log = loggers.CreateLogger<BotService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // libdave logs every MLS message (multi-kilobyte hex) to stdout by default: route it through the logger,
        // verbose/info at Debug (hidden unless Logging__LogLevel__Slopsoil.Dave=Debug), warnings and errors as such.
        var daveLog = loggers.CreateLogger("Slopsoil.Dave");
        DaveLogging.Install((severity, text) => daveLog.Log(
            severity switch
            {
                DaveLogSeverity.Error => LogLevel.Error,
                DaveLogSeverity.Warning => LogLevel.Warning,
                _ => LogLevel.Debug,
            },
            "{Message}",
            text));

        var token = options.DiscordToken!;
        var properties = new ClientProperties();
        var gatewayLog = loggers.CreateLogger<GatewayClient>();
        await using var gateway = new GatewayClient(
            new ClientWebSocketFactory(properties.WebSocketHeaders),
            new GatewayOptions { Properties = properties, Log = m => gatewayLog.LogInformation("{Message}", m) });

        using var http = new HttpClient();
        var rest = new DiscordRestClient(http, token, properties);
        var directory = new GatewayRoleDirectory(() => gateway.UserId, options.AllowedUserIds);
        var router = new CommandRouter(
            directory,
            new RestMessageSender(rest),
            new MessageHub(),
            (command, ex) => _log.LogError(ex, "command {Command} failed", command));

        // Voice + go-live.
        var voiceOptions = VoiceOptionsFor(gateway, loggers.CreateLogger("Slopsoil.Voice"));
        var voice = new VoiceManager(new GatewayVoice(gateway, voiceOptions), loggers.CreateLogger<VoiceManager>());
        using var imageHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        imageHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; slopsoil)");
        var stillLog = loggers.CreateLogger<StillImages>();
        var pipelines = new FfmpegPipelineFactory(options, loggers)
        {
            // Spec 015: loading / pause cards, fetched once at startup (a URL is downloaded to a temp file).
            Stills = new StillImages(
                await StillImageFiles.ResolveAsync(options.LoadingImage, "loading", imageHttp, stillLog, stoppingToken).ConfigureAwait(false),
                await StillImageFiles.ResolveAsync(options.PausedImage, "paused", imageHttp, stillLog, stoppingToken).ConfigureAwait(false),
                logger: stillLog),
        };
        var engine = new StreamEngine(new GoLiveConnector(gateway, voice, voiceOptions), pipelines, loggers.CreateLogger<StreamEngine>());

        // Jellyfin.
        using var jellyfinHttp = new HttpClient();
        using var jellyfin = JellyfinClient.FromOptions(options, jellyfinHttp, loggers.CreateLogger<JellyfinClient>());
        if (jellyfin is not null && DecoderSet.Detect(Slopsoil.Streaming.Process.SystemProcessRunner.Instance, loggers.CreateLogger<DecoderSet>()) is { } decoders)
        {
            // Spec 011: originals the bot's FFmpeg cannot decode go through Jellyfin's transcode.
            jellyfin.CanDecode = f => decoders.CanDecodeVideo(f.VideoCodec, f.VideoBitDepth, f.Interlaced) && decoders.CanDecodeAudio(f.AudioCodec);
        }
        IMediaLibrary? library = jellyfin is null
            ? null
            : new JellyfinLibrary(
                jellyfin,
                new HlsVariantResolver(jellyfinHttp, loggers.CreateLogger<HlsVariantResolver>()),
                new ProbeService(logger: loggers.CreateLogger<ProbeService>()),
                loggers);
        if (jellyfin is not null)
        {
            _log.LogInformation(
                "Jellyfin client configured for {Url} (SyncPlay {State})",
                jellyfin.BaseUrl,
                jellyfin.HasUserCredentials ? "enabled" : "disabled — set JELLYFIN_USERNAME/JELLYFIN_PASSWORD to enable");
        }

        var guildNames = new GuildNames();
        var media = new MediaModule(library, engine, voice, guildNames.Get, logger: loggers.CreateLogger<MediaModule>());
        var voiceModule = new VoiceModule(voice, media, loggers.CreateLogger<VoiceModule>(), emptyChannelTimeout: options.EmptyChannelTimeout);

        media.Register(router, voiceModule); // !media … plus the !join/!leave/!stop/!help/!ping shortcuts

        // Commands start in arrival order (like discord.py's single event loop) but run concurrently once they
        // await — a picker waiting 10 s must not block the next message.
        var inbox = System.Threading.Channels.Channel.CreateUnbounded<ChatMessage>(new() { SingleReader = true });
        var dispatcher = Task.Run(async () =>
        {
            await foreach (var message in inbox.Reader.ReadAllAsync(CancellationToken.None))
            {
                _ = router.DispatchAsync(message, stoppingToken);
            }
        }, CancellationToken.None);

        // Order matters: caches learn the author/guild before the router checks roles.
        gateway.Dispatch += directory.Apply;
        gateway.Dispatch += guildNames.Apply;
        gateway.Dispatch += OnDispatch;
        var gatewayTrace = loggers.CreateLogger("Slopsoil.Gateway.Voice");
        gateway.Dispatch += (type, data) =>
        {
            // Spec 012: the bot's own voice / screenshare events, which explain disconnects (never tokens).
            if (GatewayVoiceTrace.Describe(type, data, gateway.UserId, gateway.SessionId) is { } line)
            {
                gatewayTrace.LogInformation("{Event}", line);
            }
        };
        gateway.VoiceStates.Changed += (guild, user, before, after) =>
            _ = Task.Run(() => voiceModule.OnVoiceStateChangedAsync(guild, user, before, after), CancellationToken.None);

        void OnDispatch(string type, JsonElement data)
        {
            // Parse now: the JSON document is disposed once dispatch handlers return.
            if (type == "MESSAGE_CREATE" && ChatMessageParser.TryParse(data, out var message))
            {
                inbox.Writer.TryWrite(message);
            }
        }

        _log.LogInformation("Configuration: {Options}", options);
        if (!pipelines.EncoderAvailable)
        {
            _log.LogWarning("no working H.264 encoder found — go-live streaming is unavailable");
        }

        try
        {
            await gateway.ConnectAsync(token, stoppingToken);
            _log.LogInformation("Logged in as user {UserId}", gateway.UserId);
            await gateway.Completion.WaitAsync(stoppingToken);
            _log.LogError("Gateway connection ended; stopping");
            lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Gateway failed; stopping");
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
        finally
        {
            inbox.Writer.TryComplete();
            await ShutdownStreamsAsync(engine, media, voice);
            await dispatcher.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// <c>bot.close()</c>: stop every stream (FFmpeg killed, STREAM_DELETE sent, SyncPlay left, transcodes released)
    /// and leave voice, bounded so a stuck connection can't hang the shutdown.
    /// </summary>
    private async Task ShutdownStreamsAsync(StreamEngine engine, MediaModule media, VoiceManager voice)
    {
        var guilds = engine.ActiveGuilds.Concat(voice.Guilds).Distinct().ToList();
        if (guilds.Count == 0)
        {
            return;
        }

        _log.LogInformation("shutting down: stopping streams in {Count} guild(s)", guilds.Count);
        var stops = guilds.Select(async g =>
        {
            await media.StopAsync(g, "shutdown");
            await voice.LeaveAsync(g);
        });
        await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private static VoiceOptions VoiceOptionsFor(GatewayClient gateway, ILogger log)
    {
        int daveVersion;
        Func<IDaveSession>? daveFactory;
        try
        {
            daveVersion = LibDaveSession.MaxSupportedProtocolVersion;
            daveFactory = () => new LibDaveSession(daveVersion, 0, 0, m => log.LogInformation("{Message}", m));
        }
        catch (DllNotFoundException)
        {
            log.LogWarning("libdave.so not found: DAVE disabled (DAVE-required channels will refuse the connection)");
            daveVersion = 0;
            daveFactory = null;
        }

        return new VoiceOptions
        {
            WebSockets = new ClientWebSocketFactory(),
            Udp = new UdpClientFactory(),
            DaveSessionFactory = daveFactory,
            MaxDaveProtocolVersion = daveVersion,
            ChannelMembers = (guild, channel) => gateway.VoiceStates.GetGuild(guild).Where(kv => kv.Value == channel).Select(kv => kv.Key),
            Log = m => log.LogInformation("{Message}", m),
            Trace = m => log.LogInformation("{Message}", m),
        };
    }

    private sealed class RestMessageSender(DiscordRestClient rest) : IMessageSender
    {
        public Task SendAsync(ulong channelId, string content, CancellationToken ct) => rest.SendMessageAsync(channelId, content, ct);
    }
}

/// <summary>Turns <c>STREAM_LOADING_IMAGE</c> / <c>STREAM_PAUSED_IMAGE</c> (a path or an http(s) URL) into a local file.</summary>
internal static class StillImageFiles
{
    public static async Task<string?> ResolveAsync(string? value, string name, HttpClient http, ILogger log, CancellationToken ct)
    {
        if (value is null)
        {
            return null;
        }

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(value))
            {
                log.LogInformation("{Name} image: {Path}", name, value);
                return value;
            }

            log.LogWarning("{Name} image {Path} not found — no {Name} card", name, value, name);
            return null;
        }

        try
        {
            var bytes = await http.GetByteArrayAsync(value, ct).ConfigureAwait(false);
            var extension = Path.GetExtension(new Uri(value).AbsolutePath);
            var path = Path.Combine(Path.GetTempPath(), $"slopsoil_{name}_card{(extension.Length is > 0 and <= 5 ? extension : ".img")}");
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            log.LogInformation("{Name} image downloaded ({Bytes} bytes) from {Url}", name, bytes.Length, Slopsoil.Common.Logging.Redactor.RedactUrl(value));
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
        {
            log.LogWarning("could not download the {Name} image from {Url}: {Error} — no {Name} card", name, Slopsoil.Common.Logging.Redactor.RedactUrl(value), ex.Message, name);
            return null;
        }
    }
}

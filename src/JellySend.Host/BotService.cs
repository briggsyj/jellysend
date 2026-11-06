using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellySend.Commands.Events;
using JellySend.Common.Config;
using JellySend.Commands.Media;
using JellySend.Commands.Routing;
using JellySend.Commands.Voice;
using JellySend.Discord.Dave;
using JellySend.Discord.Gateway;
using JellySend.Discord.Native;
using JellySend.Discord.Rest;
using JellySend.Discord.Transport;
using JellySend.Discord.Voice;
using JellySend.Services.Jellyfin;
using JellySend.Services.Media;
using JellySend.Streaming.Engine;
using JellySend.Streaming.Video;

namespace JellySend.Host;

/// <summary>
/// The bot process (<c>bot.py</c>): log in over the gateway, keep the role directory current, and run
/// <c>!</c> commands from MESSAGE_CREATE, replying over REST. Wiring only; the parts are unit-tested.
/// </summary>
internal sealed class BotService(JellySendOptions options, ILoggerFactory loggers, IHostApplicationLifetime lifetime) : BackgroundService
{
    private readonly ILogger _log = loggers.CreateLogger<BotService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // libdave logs every MLS message (multi-kilobyte hex) to stdout by default: route it through the logger,
        // verbose/info at Debug (hidden unless Logging__LogLevel__JellySend.Dave=Debug), warnings and errors as such.
        var daveLog = loggers.CreateLogger("JellySend.Dave");
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

        // Spec 024: every command that reaches the bot is logged, so a missed one can be told from an ignored one.
        router.OnCommand = (message, resolved) => _log.LogInformation(
            "command {Resolved} from {User} in guild {Guild} channel {Channel}: {Text}",
            resolved, message.AuthorId, message.GuildId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "DM",
            message.ChannelId, message.Content.Length > 200 ? message.Content[..200] + "…" : message.Content);

        // Voice + go-live.
        var voiceOptions = VoiceOptionsFor(gateway, loggers.CreateLogger("JellySend.Voice"));
        var voice = new VoiceManager(new GatewayVoice(gateway, voiceOptions, rest), loggers.CreateLogger<VoiceManager>());
        var stillLog = loggers.CreateLogger<StillImages>();
        var pipelines = new FfmpegPipelineFactory(options, loggers)
        {
            // Spec 015: loading / pause cards — the ones bundled in the image (assets/cards), or local files.
            Stills = new StillImages(
                StillImageFiles.Resolve(options.LoadingImage, "loading", stillLog),
                StillImageFiles.Resolve(options.PausedImage, "paused", stillLog),
                logger: stillLog),
        };
        var engine = new StreamEngine(new GoLiveConnector(gateway, voice, voiceOptions), pipelines, loggers.CreateLogger<StreamEngine>());

        // Jellyfin.
        using var jellyfinHttp = new HttpClient();
        using var jellyfin = JellyfinClient.FromOptions(options, jellyfinHttp, loggers.CreateLogger<JellyfinClient>());
        if (jellyfin is not null && DecoderSet.Detect(JellySend.Streaming.Process.SystemProcessRunner.Instance, loggers.CreateLogger<DecoderSet>()) is { } decoders)
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
        var settingsLog = loggers.CreateLogger<SettingsStore>();
        var settings = new SettingsStore(options.DataDirectory, m => settingsLog.LogWarning("{Message}", m));
        var media = new MediaModule(library, engine, voice, guildNames.Get, logger: loggers.CreateLogger<MediaModule>())
        {
            MaxPlaytime = options.MaxPlaytime,
            Settings = settings,
            Announcer = new RestMessageSender(rest),
        };
        var voiceModule = new VoiceModule(
            voice, media, loggers.CreateLogger<VoiceModule>(), emptyChannelTimeout: options.EmptyChannelTimeout, announcer: new RestMessageSender(rest));

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

        // Spec 028/034: Discord events with a "Screenings" list start the bot; changes arrive pushed over the gateway.
        var events = new EventScheduler(
            new RestEventSource(rest), media, directory, new RestMessageSender(rest), logger: loggers.CreateLogger<EventScheduler>());
        gateway.Dispatch += events.Apply;
        media.UpcomingEvents = events.Upcoming; // spec 036: !schedule events
        var gatewayTrace = loggers.CreateLogger("JellySend.Gateway.Voice");
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
            _ = events.RunAsync(stoppingToken);
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

    private sealed class RestMessageSender(DiscordRestClient rest) : IMessageSender, ITypingIndicator
    {
        public Task SendAsync(ulong channelId, string content, CancellationToken ct) => rest.SendMessageAsync(channelId, content, ct);

        public Task TypingAsync(ulong channelId, CancellationToken ct) => rest.TriggerTypingAsync(channelId, ct);
    }
}

/// <summary>
/// Picks the card image for <c>STREAM_LOADING_IMAGE</c> / <c>STREAM_PAUSED_IMAGE</c> (spec 015): a local file path, <c>none</c>
/// to turn the card off, or (unset) the card bundled in the image under <c>cards/</c>. Nothing is downloaded.
/// </summary>
internal static class StillImageFiles
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp"];

    public static string? Resolve(string? value, string name, ILogger log)
    {
        if (value is not null && value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("{Name} card turned off", name);
            return null;
        }

        if (value is not null)
        {
            if (File.Exists(value))
            {
                log.LogInformation("{Name} card: {Path}", name, value);
                return value;
            }

            log.LogWarning("{Name} card {Path} not found — using the bundled card if there is one", name, value);
        }

        var bundled = Extensions.Select(e => Path.Combine(AppContext.BaseDirectory, "cards", name + e)).FirstOrDefault(File.Exists);
        if (bundled is not null)
        {
            log.LogInformation("{Name} card: bundled {Path}", name, bundled);
        }
        else
        {
            log.LogInformation("no {Name} card (none bundled in cards/ and STREAM_{Upper}_IMAGE unset)", name, name.ToUpperInvariant());
        }

        return bundled;
    }
}

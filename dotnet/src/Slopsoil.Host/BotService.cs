using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Slopsoil.Common.Config;
using Slopsoil.Commands.General;
using Slopsoil.Commands.Media;
using Slopsoil.Commands.Routing;
using Slopsoil.Commands.Voice;
using Slopsoil.Discord.Dave;
using Slopsoil.Discord.Gateway;
using Slopsoil.Discord.Rest;
using Slopsoil.Discord.Transport;
using Slopsoil.Discord.Voice;
using Slopsoil.Services.Jellyfin;
using Slopsoil.Services.Media;
using Slopsoil.Streaming.Engine;

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
        var voiceOptions = VoiceOptionsFor(loggers.CreateLogger("Slopsoil.Voice"));
        var voice = new VoiceManager(new GatewayVoice(gateway, voiceOptions), loggers.CreateLogger<VoiceManager>());
        var pipelines = new FfmpegPipelineFactory(options, loggers);
        var engine = new StreamEngine(new GoLiveConnector(gateway, voice, voiceOptions), pipelines, loggers.CreateLogger<StreamEngine>());

        // Jellyfin.
        using var jellyfinHttp = new HttpClient();
        using var jellyfin = JellyfinClient.FromOptions(options, jellyfinHttp, loggers.CreateLogger<JellyfinClient>());
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
        var voiceModule = new VoiceModule(voice, media, loggers.CreateLogger<VoiceModule>());

        GeneralModule.Register(router, () => library is not null);
        voiceModule.Register(router);
        media.Register(router);
        router.AddUnavailablePlaceholders();

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

    private static VoiceOptions VoiceOptionsFor(ILogger log)
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
            Log = m => log.LogInformation("{Message}", m),
        };
    }

    private sealed class RestMessageSender(DiscordRestClient rest) : IMessageSender
    {
        public Task SendAsync(ulong channelId, string content, CancellationToken ct) => rest.SendMessageAsync(channelId, content, ct);
    }
}

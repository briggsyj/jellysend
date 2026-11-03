using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Slopsoil.Common.Config;
using Slopsoil.Commands.General;
using Slopsoil.Commands.Routing;
using Slopsoil.Discord.Gateway;
using Slopsoil.Discord.Rest;
using Slopsoil.Discord.Transport;

namespace Slopsoil.Host;

/// <summary>
/// The bot process (<c>bot.py</c>): log in over the gateway, keep the role directory current, and run
/// <c>!</c> commands from MESSAGE_CREATE, replying over REST. Wiring only; the parts are unit-tested.
/// </summary>
internal sealed class BotService(SlopsoilOptions options, ILogger<BotService> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = options.DiscordToken!;
        var properties = new ClientProperties();
        var gatewayLog = (string m) => logger.LogInformation("{Message}", m);
        await using var gateway = new GatewayClient(
            new ClientWebSocketFactory(properties.WebSocketHeaders),
            new GatewayOptions { Properties = properties, Log = gatewayLog });

        using var http = new HttpClient();
        var rest = new DiscordRestClient(http, token, properties);
        var directory = new GatewayRoleDirectory(() => gateway.UserId, options.AllowedUserIds);
        var router = new CommandRouter(
            directory,
            new RestMessageSender(rest),
            new MessageHub(),
            (command, ex) => logger.LogError(ex, "command {Command} failed", command));
        GeneralModule.Register(router, () => options.JellyfinConfigured);
        router.AddUnavailablePlaceholders();

        // Order matters: the directory learns the author before the router checks their role.
        gateway.Dispatch += directory.Apply;
        gateway.Dispatch += OnDispatch;

        void OnDispatch(string type, JsonElement data)
        {
            // Parse now: the JSON document is disposed once dispatch handlers return.
            if (type == "MESSAGE_CREATE" && ChatMessageParser.TryParse(data, out var message))
            {
                _ = Task.Run(() => router.DispatchAsync(message, stoppingToken), CancellationToken.None);
            }
        }

        logger.LogInformation("Configuration: {Options}", options);
        try
        {
            await gateway.ConnectAsync(token, stoppingToken);
            logger.LogInformation("Logged in as user {UserId}", gateway.UserId);
            await gateway.Completion.WaitAsync(stoppingToken);
            logger.LogError("Gateway connection ended; stopping");
            lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Gateway failed; stopping");
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }

    private sealed class RestMessageSender(DiscordRestClient rest) : IMessageSender
    {
        public Task SendAsync(ulong channelId, string content, CancellationToken ct) => rest.SendMessageAsync(channelId, content, ct);
    }
}

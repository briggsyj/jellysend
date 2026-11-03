using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Slopsoil.Common.Config;
using Slopsoil.Common.Logging;
using Slopsoil.Host;

if (Environment.GetEnvironmentVariable("SLOPSOIL_SPIKE") == "testpattern")
{
    var cts = new CancellationTokenSource(); // not disposed: ProcessExit may still cancel it
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
    return await Spike.RunAsync(cts.Token);
}

var builder = Host.CreateApplicationBuilder(args);

// Console logging, every message and exception passed through the redactor (FR-041).
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
var console = builder.Services.Single(d => d.ServiceType == typeof(ILoggerProvider) && d.ImplementationType == typeof(ConsoleLoggerProvider));
builder.Services.Remove(console);
builder.Services.AddSingleton<ConsoleLoggerProvider>();
builder.Services.AddSingleton<ILoggerProvider>(sp => new RedactingLoggerProvider(sp.GetRequiredService<ConsoleLoggerProvider>()));

var warnings = new List<string>();
var options = SlopsoilOptions.FromEnvironment(warnings.Add);
builder.Services.AddSingleton(options);
builder.Services.AddHostedService<BotService>();

using var host = builder.Build();
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Slopsoil");
foreach (var warning in warnings)
{
    log.LogWarning("{Warning}", warning);
}

if (options.DiscordToken is null)
{
    log.LogCritical("DISCORD_TOKEN environment variable not set");
    return 1;
}

if (!options.JellyfinConfigured)
{
    log.LogWarning("JELLYFIN_URL/API_KEY not set — !media will report unconfigured");
}

await host.RunAsync();
return Environment.ExitCode;

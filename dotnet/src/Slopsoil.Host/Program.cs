using Slopsoil.Host;

var cts = new CancellationTokenSource(); // not disposed: ProcessExit may still cancel it
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

if (Environment.GetEnvironmentVariable("SLOPSOIL_SPIKE") == "testpattern")
{
    return await Spike.RunAsync(cts.Token);
}

Console.Error.WriteLine("The .NET bot is not feature-complete yet. Set SLOPSOIL_SPIKE=testpattern to run the go-live spike.");
return 1;

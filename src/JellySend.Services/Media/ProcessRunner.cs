using System.Diagnostics;

namespace JellySend.Services.Media;

/// <summary>Exit code and captured output of a finished process.</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="StdOut">Captured standard output.</param>
/// <param name="StdErr">Captured standard error.</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Seam for running a short-lived tool (ffprobe) and capturing its output.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Run <paramref name="fileName"/> with <paramref name="arguments"/> to completion.
    /// Throws <see cref="TimeoutException"/> (after killing the process) when it outlives <paramref name="timeout"/>.
    /// </summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary><see cref="IProcessRunner"/> over <see cref="Process"/>.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <summary>Shared instance.</summary>
    public static SystemProcessRunner Instance { get; } = new();

    /// <inheritdoc/>
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited in the meantime.
            }

            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{fileName} did not exit within {timeout.TotalSeconds:0.#} s");
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}

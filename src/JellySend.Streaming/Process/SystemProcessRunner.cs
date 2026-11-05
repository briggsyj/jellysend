using System.Diagnostics;
using SysProcess = System.Diagnostics.Process;

namespace JellySend.Streaming.Process;

/// <summary><see cref="IProcessRunner"/> over <see cref="System.Diagnostics.Process"/>.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public static readonly SystemProcessRunner Instance = new();

    public IMediaProcess Start(IReadOnlyList<string> args) => new SystemMediaProcess(Launch(args));

    public ProcessResult Run(IReadOnlyList<string> args, TimeSpan timeout)
    {
        using var process = Launch(args);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"{args[0]} did not finish within {timeout.TotalSeconds:0.#} s");
        }

        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static SysProcess Launch(IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(args[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args.Skip(1))
        {
            info.ArgumentList.Add(arg);
        }

        var process = SysProcess.Start(info)!;
        process.StandardInput.Close(); // stdin at EOF, like subprocess.DEVNULL
        return process;
    }

    private sealed class SystemMediaProcess(SysProcess process) : IMediaProcess
    {
        public Stream StandardOutput { get; } = process.StandardOutput.BaseStream;

        public TextReader StandardError => process.StandardError;

        public bool HasExited => process.HasExited;

        public int ExitCode => process.ExitCode;

        public void CloseStandardOutput() => StandardOutput.Dispose();

        public void Terminate()
        {
            if (!process.HasExited)
            {
                _ = LibC.Kill(process.Id, LibC.SigTerm);
            }
        }

        public void Kill()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
        }

        public bool WaitForExit(TimeSpan timeout) =>
            timeout == Timeout.InfiniteTimeSpan ? WaitForever() : process.WaitForExit(timeout);

        public void Dispose() => process.Dispose();

        private bool WaitForever()
        {
            process.WaitForExit();
            return true;
        }
    }
}

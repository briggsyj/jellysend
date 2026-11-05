namespace JellySend.Streaming.Process;

/// <summary>Output of a short-lived process run to completion (encoder probes).</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Starts child processes (FFmpeg). The seam that lets the video player and encoder detection run
/// without a real FFmpeg in tests. <c>args[0]</c> is the executable.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Start a long-running process with stdin at EOF (like <c>subprocess.DEVNULL</c>) and stdout/stderr piped.
    /// Throws <see cref="System.ComponentModel.Win32Exception"/> when the executable is missing.
    /// </summary>
    IMediaProcess Start(IReadOnlyList<string> args);

    /// <summary>
    /// Run a process to completion. Throws <see cref="System.ComponentModel.Win32Exception"/> when the
    /// executable is missing and <see cref="TimeoutException"/> (after killing it) when it overruns.
    /// </summary>
    ProcessResult Run(IReadOnlyList<string> args, TimeSpan timeout);
}

/// <summary>A running child process with piped output.</summary>
public interface IMediaProcess : IDisposable
{
    /// <summary>Raw stdout (H.264 Annex-B for the video player).</summary>
    Stream StandardOutput { get; }

    /// <summary>stderr as text lines.</summary>
    TextReader StandardError { get; }

    bool HasExited { get; }

    /// <summary>Exit code once exited.</summary>
    int ExitCode { get; }

    /// <summary>Close our end of stdout so a child blocked writing to a full pipe gets EPIPE.</summary>
    void CloseStandardOutput();

    /// <summary>SIGTERM.</summary>
    void Terminate();

    /// <summary>SIGKILL.</summary>
    void Kill();

    /// <summary>True if the process exited within <paramref name="timeout"/>.</summary>
    bool WaitForExit(TimeSpan timeout);
}

/// <summary>Creates and removes the named pipe FFmpeg writes PCM audio into.</summary>
public interface IFifoFactory
{
    /// <summary>Create the FIFO, replacing any file already at <paramref name="path"/>.</summary>
    void Create(string path);

    /// <summary>Remove the FIFO; a missing file is fine.</summary>
    void Remove(string path);
}

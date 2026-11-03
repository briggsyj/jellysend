using System.Runtime.InteropServices;

namespace Slopsoil.Streaming.Process;

/// <summary>The two libc calls .NET has no managed API for.</summary>
internal static partial class LibC
{
    public const int SigTerm = 15;

    [LibraryImport("libc", EntryPoint = "mkfifo", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int MkFifo(string path, uint mode);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int pid, int signal);
}

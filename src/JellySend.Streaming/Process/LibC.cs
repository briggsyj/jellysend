using System.Runtime.InteropServices;

namespace JellySend.Streaming.Process;

/// <summary>The libc calls .NET has no managed API for.</summary>
internal static partial class LibC
{
    public const int SigTerm = 15;

    [LibraryImport("libc", EntryPoint = "mkfifo", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int MkFifo(string path, uint mode);

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int Link(string existing, string newPath);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int pid, int signal);
}

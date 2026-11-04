using System.Runtime.InteropServices;

namespace Slopsoil.Streaming.Audio;

/// <summary>The one libc call the audio path needs: bytes buffered in a pipe/FIFO (<c>ioctl(FIONREAD)</c>).</summary>
internal static partial class PosixIo
{
    private const nuint FionreadLinux = 0x541B;
    private const nuint FionreadBsd = 0x4004667F;

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int fd, nuint request, out int value);

    /// <summary>Bytes readable without blocking, or -1 if the descriptor can't be queried.</summary>
    public static int BytesAvailable(SafeHandle fd)
    {
        var added = false;
        try
        {
            fd.DangerousAddRef(ref added);
            var request = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? FionreadBsd : FionreadLinux;
            return Ioctl((int)fd.DangerousGetHandle(), request, out var value) == 0 ? value : -1;
        }
        finally
        {
            if (added)
            {
                fd.DangerousRelease();
            }
        }
    }
}

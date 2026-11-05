using System.ComponentModel;
using System.Runtime.InteropServices;

namespace JellySend.Streaming.Process;

/// <summary>Named pipes via libc <c>mkfifo</c> (<c>H264VideoPlayer._ensure_fifo</c> / <c>_cleanup_fifo</c>).</summary>
public sealed class UnixFifo : IFifoFactory
{
    public static readonly UnixFifo Instance = new();

    public void Create(string path)
    {
        Remove(path);
        if (LibC.MkFifo(path, 0x180 /* 0600 */) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"mkfifo {path} failed");
        }
    }

    public void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

namespace JellySend.Streaming.Diagnostics;

/// <summary>
/// The text file FFmpeg's <c>drawtext=textfile=…:reload=1</c> reads every frame. Rewritten atomically
/// (write a sibling then rename over it) so FFmpeg never reads a half-written file.
/// </summary>
public static class OverlayFile
{
    /// <summary>Atomic replace. A unique temp name per write: the 1 s timer and a restart may write concurrently.</summary>
    public static void Write(string path, string text)
    {
        var tmp = $"{path}.{Environment.CurrentManagedThreadId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void Delete(string path)
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

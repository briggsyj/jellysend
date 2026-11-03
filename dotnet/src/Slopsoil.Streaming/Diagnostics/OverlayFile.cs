namespace Slopsoil.Streaming.Diagnostics;

/// <summary>
/// The text file FFmpeg's <c>drawtext=textfile=…:reload=1</c> reads every frame. Rewritten atomically
/// (write a sibling then rename over it) so FFmpeg never reads a half-written file.
/// </summary>
public static class OverlayFile
{
    public static void Write(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

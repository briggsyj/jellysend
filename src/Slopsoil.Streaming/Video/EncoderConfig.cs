using System.Globalization;
using System.Text.RegularExpressions;

namespace Slopsoil.Streaming.Video;

/// <summary>One H.264 encoder setup (<c>video_player._EncoderConfig</c>).</summary>
/// <param name="Name">FFmpeg encoder name (<c>-c:v</c>).</param>
/// <param name="PreInput">Arguments before <c>-i</c> (e.g. the VA-API device).</param>
/// <param name="PostCodec">Arguments after <c>-c:v name</c>.</param>
/// <param name="Vf">The <c>-vf</c> chain (fit-to-box scaler, hwupload for VA-API).</param>
public sealed record EncoderConfig(string Name, IReadOnlyList<string> PreInput, IReadOnlyList<string> PostCodec, string Vf)
{
    public const string Nvenc = "h264_nvenc";
    public const string Vaapi = "h264_vaapi";
    public const string Libx264 = "libx264";
    public const string Libopenh264 = "libopenh264";

    /// <summary>The device the VA-API encoder opens.</summary>
    public const string VaapiDevice = "/dev/dri/renderD128";

    public bool IsHardware => Name is Nvenc or Vaapi;

    public static EncoderConfig ForNvenc(string resolution, string bitrate) => new(
        Nvenc,
        [],
        [
            "-preset", "p1", "-tune", "ll", "-profile:v", "high", "-level:v", "4.2", "-aud", "1",
            "-bf", "0", // no B-frames: see ForVaapi
            "-rc", "cbr", "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bitrate,
        ],
        FfmpegFilters.FitScale(resolution));

    public static EncoderConfig ForVaapi(string resolution, string bitrate) => new(
        Vaapi,
        ["-vaapi_device", VaapiDevice],
        [
            "-rc_mode", "CBR", "-profile:v", "high", "-level", "42", "-aud", "1",
            // h264_vaapi defaults to 2 B-frames. Frames are sent in decode order with evenly spaced RTP
            // timestamps, so B-frames make receivers show them out of order: judder even at low load.
            "-bf", "0",
            "-b:v", bitrate,
        ],
        FfmpegFilters.VaapiUpload + FfmpegFilters.FitScale(resolution, "scale_vaapi"));

    public static EncoderConfig ForLibx264(string resolution, string bitrate) => new(
        Libx264,
        [],
        [
            "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "high", "-level:v", "4.2",
            "-x264-params", "aud=1", "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bitrate,
        ],
        FfmpegFilters.FitScale(resolution));

    /// <summary>
    /// Software encoder: the primary without a working GPU encoder and the runtime fallback when a hardware
    /// encoder accepts no frames (e.g. VA-API on large MPEG-2 keyframes).
    /// </summary>
    public static EncoderConfig ForLibopenh264(string resolution, string bitrate) => new(
        Libopenh264,
        [],
        ["-profile:v", "constrained_baseline", "-level:v", "4.2", "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bitrate],
        FfmpegFilters.FitScale(resolution));
}

/// <summary>Scaler filter expressions (<c>_fit_scale</c>, <c>_with_aspect</c>).</summary>
public static partial class FfmpegFilters
{
    /// <summary>Prefix of the VA-API chain: frames go to a GPU surface before <c>scale_vaapi</c>.</summary>
    public const string VaapiUpload = "format=nv12,hwupload,";

    /// <summary>
    /// Fit the source inside <paramref name="resolution"/> (<c>W:H</c>) keeping its display aspect ratio
    /// (<c>dar</c> includes non-square pixels), with even dimensions and square output pixels.
    /// </summary>
    public static string FitScale(string resolution, string scaler = "scale")
    {
        var parts = resolution.Split(':');
        var (w, h) = (parts[0], parts[1]);
        var box = $"{w}/{h}";
        return $"{scaler}=w='if(gt(dar,{box}),{w},2*round({h}*dar/2))'"
            + $":h='if(gt(dar,{box}),2*round({w}/dar/2),{h})',setsar=1";
    }

    /// <summary>Force a display aspect ratio in a <see cref="FitScale"/> chain; null keeps the source's.</summary>
    public static string WithAspect(string vf, double? aspect) =>
        aspect is { } a ? Dar().Replace(vf, a.ToString("F10", CultureInfo.InvariantCulture)) : vf;

    [GeneratedRegex(@"\bdar\b")]
    private static partial Regex Dar();
}

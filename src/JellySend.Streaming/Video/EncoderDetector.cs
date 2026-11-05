using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using JellySend.Common.Logging;
using JellySend.Streaming.Process;

namespace JellySend.Streaming.Video;

/// <summary>The encoder to use and the software encoder to retry with if it produces no frames.</summary>
public sealed record EncoderSelection(EncoderConfig Primary, EncoderConfig? Fallback);

/// <summary>
/// Picks the best working H.264 encoder: NVENC → VA-API → libx264 → libopenh264, each gated on a real
/// one-frame probe so a compiled-in but unusable encoder is skipped (<c>_detect_encoder</c>).
/// </summary>
public sealed class EncoderDetector(IProcessRunner processes, ILogger? logger = null, string executable = "ffmpeg")
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _log = logger ?? NullLogger.Instance;

    /// <summary>The best encoder, or null (no ffmpeg / nothing works) — video streaming is then disabled.</summary>
    public EncoderSelection? Detect(string resolution, string bitrate)
    {
        string available;
        try
        {
            available = processes.Run([executable, "-hide_banner", "-encoders"], ProbeTimeout).StandardOutput;
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException)
        {
            _log.LogWarning(
                "ffmpeg not found — video streaming disabled. Install ffmpeg (Fedora: ffmpeg-free, Debian/Ubuntu: ffmpeg).");
            return null;
        }

        var primary = DetectPrimary(available, resolution, bitrate);
        if (primary is null)
        {
            _log.LogWarning(
                "no working H.264 encoder found — video streaming disabled. Ensure ffmpeg-free (or equivalent) is "
                + "installed with libopenh264, or that VA-API/NVENC drivers are functional.");
            return null;
        }

        // Runtime fallback when the primary accepts no frames: libopenh264, or libx264 behind a GPU encoder
        // when libopenh264 is not compiled in.
        EncoderConfig? fallback = null;
        if (primary.Name != EncoderConfig.Libopenh264 && available.Contains(EncoderConfig.Libopenh264, StringComparison.Ordinal))
        {
            fallback = EncoderConfig.ForLibopenh264(resolution, bitrate);
        }
        else if (primary.IsHardware && available.Contains(EncoderConfig.Libx264, StringComparison.Ordinal))
        {
            fallback = EncoderConfig.ForLibx264(resolution, bitrate);
        }

        return new EncoderSelection(primary, fallback);
    }

    /// <summary>
    /// True if FFmpeg can really encode one frame with <paramref name="name"/>. Uses the same <c>-vf</c> as the
    /// real pipeline (VA-API needs frames on a hardware surface); failures are logged at debug with the stderr tail.
    /// </summary>
    public bool TestEncoder(string name, IReadOnlyList<string> preInput, string vf = "")
    {
        List<string> cmd = [executable, "-hide_banner", .. preInput, "-f", "lavfi", "-i", "nullsrc=size=64x64:rate=1", "-vframes", "1"];
        if (vf.Length > 0)
        {
            cmd.AddRange(["-vf", vf]);
        }

        cmd.AddRange(["-c:v", name, "-f", "null", "-"]);
        try
        {
            var result = processes.Run(cmd, ProbeTimeout);
            if (result.ExitCode != 0)
            {
                var tail = result.StandardError.Trim().Split('\n').TakeLast(3).Select(l => l.Trim());
                _log.LogDebug(
                    "encoder {Name} probe failed (exit {Code}): {Tail}", name, result.ExitCode, Redactor.RedactText(string.Join(" | ", tail)));
            }

            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or IOException or InvalidOperationException)
        {
            _log.LogDebug("encoder {Name} probe error: {Error}", name, ex.Message);
            return false;
        }
    }

    private EncoderConfig? DetectPrimary(string available, string resolution, string bitrate)
    {
        bool Listed(string name) => available.Contains(name, StringComparison.Ordinal);

        // Hardware is preferred so a working GPU is always used when present.
        if (Listed(EncoderConfig.Nvenc))
        {
            if (TestEncoder(EncoderConfig.Nvenc, []))
            {
                _log.LogInformation("video encoder: h264_nvenc (NVIDIA)");
                return EncoderConfig.ForNvenc(resolution, bitrate);
            }

            _log.LogInformation("h264_nvenc is compiled in but unavailable (no usable NVIDIA GPU/driver) — skipping");
        }

        if (Listed(EncoderConfig.Vaapi))
        {
            var vaapi = EncoderConfig.ForVaapi(resolution, bitrate);
            if (TestEncoder(vaapi.Name, vaapi.PreInput, vaapi.Vf))
            {
                _log.LogInformation("video encoder: h264_vaapi (VA-API)");
                return vaapi;
            }

            _log.LogWarning(
                "h264_vaapi is compiled in but the VA-API probe failed — falling back to software. Likely a missing GPU "
                + "driver (AMD needs mesa-va-drivers-freeworld, Intel needs intel-media-driver) or no /dev/dri access. "
                + "Run `vainfo` in the container to diagnose; enable DEBUG logging to see the FFmpeg error.");
        }

        if (Listed(EncoderConfig.Libx264) && TestEncoder(EncoderConfig.Libx264, []))
        {
            _log.LogInformation("video encoder: libx264 (software)");
            return EncoderConfig.ForLibx264(resolution, bitrate);
        }

        if (Listed(EncoderConfig.Libopenh264) && TestEncoder(EncoderConfig.Libopenh264, []))
        {
            _log.LogInformation("video encoder: libopenh264 (software)");
            return EncoderConfig.ForLibopenh264(resolution, bitrate);
        }

        return null;
    }
}

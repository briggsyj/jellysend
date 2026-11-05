using System.Globalization;
using System.Text;

namespace Slopsoil.Streaming.Video;

/// <summary>Inputs of one FFmpeg run (one playback segment).</summary>
public sealed record FfmpegCommandOptions
{
    /// <summary>Input URL or local path (may carry credentials: never log unredacted).</summary>
    public required string Url { get; init; }

    public required EncoderConfig Encoder { get; init; }

    /// <summary>FIFO FFmpeg writes s16le 48 kHz stereo PCM into.</summary>
    public required string AudioFifo { get; init; }

    /// <summary>Exact output rate (e.g. 29.97); the media clock counts frames at this rate.</summary>
    public double Fps { get; init; } = 30.0;

    /// <summary>Live input (no <c>-re</c>); <c>null</c> means unknown and is treated like VOD for local paths.</summary>
    public bool? Live { get; init; } = true;

    /// <summary>The source has an audio track; otherwise silence is generated so the FIFO always has a writer.</summary>
    public bool HasAudio { get; init; } = true;

    /// <summary>Which of the input's own audio tracks to play (0 = first, FFmpeg <c>0:a:N</c>; spec 013); null = the first.</summary>
    public int? AudioTrack { get; init; }

    public long ProbeSize { get; init; } = 2_000_000;

    /// <summary>Input seek in seconds (<c>-ss</c> before <c>-i</c>); none when ≤ 0.</summary>
    public double Offset { get; init; }

    /// <summary>Forced display aspect ratio; null keeps the source's.</summary>
    public double? Aspect { get; init; }

    /// <summary>When set, burn the debug overlay from this text file (reloaded every frame).</summary>
    public string? OverlayFile { get; init; }

    /// <summary>Overlay text size in pixels.</summary>
    public int OverlayFontSize { get; init; } = 24;

    public string Executable { get; init; } = "ffmpeg";
}

/// <summary>Builds the FFmpeg argument list exactly as <c>H264VideoPlayer._ffmpeg_cmd</c> does.</summary>
public static class FfmpegCommand
{
    private static readonly string[] UrlSchemes = ["http://", "https://", "rtmp://", "rtsp://"];

    public static bool IsUrl(string input) => UrlSchemes.Any(s => input.StartsWith(s, StringComparison.Ordinal));

    /// <summary>The full argument list; element 0 is the executable.</summary>
    public static IReadOnlyList<string> Build(FfmpegCommandOptions o)
    {
        var inv = CultureInfo.InvariantCulture;
        var isUrl = IsUrl(o.Url);
        var probe = o.ProbeSize.ToString(inv);
        var args = new List<string>
        {
            o.Executable,
            "-y", // overwrite the FIFO without an interactive prompt
            "-loglevel", "warning",
            // Tolerate initial decode errors (mpeg2video frames before the first sequence header).
            "-max_error_rate", "1",
        };
        args.AddRange(o.Encoder.PreInput);
        args.AddRange(["-probesize", probe, "-analyzeduration", probe, "-fflags", "+discardcorrupt"]);

        // Pace a local VOD file at native rate; live inputs / URLs are already real-time.
        if (o.Live != true && !isUrl)
        {
            args.Add("-re");
        }

        // -reconnect flags are HTTP-only; FFmpeg rejects them for local files.
        if (isUrl)
        {
            args.AddRange(["-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5"]);
        }

        if (o.Offset > 0)
        {
            args.AddRange(["-ss", o.Offset.ToString("F3", inv)]);
        }

        args.AddRange(["-i", o.Url, "-map", "0:v:0", "-vf", VideoFilter(o), "-c:v", o.Encoder.Name]);
        args.AddRange(o.Encoder.PostCodec);
        args.AddRange(
        [
            // Exact constant rate: the playout clock sends frame n at t0 + n/Fps. CFR also makes frame 0 output
            // time 0 (the segment start), duplicating the first picture when the video starts after the audio.
            "-fps_mode", "cfr",
            "-r", FrameRate.ToFfmpeg(o.Fps),
            "-g", Math.Max(1, (int)Math.Round(o.Fps)).ToString(inv),
            "-f", "h264", "pipe:1",
        ]);

        // No audio track: a lavfi silence input keeps the FIFO writer alive so the audio sender never hangs.
        if (!o.HasAudio)
        {
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
        }

        args.AddRange(
        [
            "-map", !o.HasAudio ? "1:a:0" : o.AudioTrack is { } audio ? $"0:a:{audio.ToString(CultureInfo.InvariantCulture)}" : "0:a:0",
            // One media timeline (spec 004 FR-001): the raw PCM has no timestamps, so pad/trim it to output time 0
            // (sample 0 = video frame 0 = the -ss point) and fill timestamp gaps with silence. Without this a
            // source whose audio starts 0.5 s after its video plays 0.5 s early.
            "-af", AudioFilter,
            "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2", "-f", "s16le", o.AudioFifo,
        ]);
        return args;
    }

    /// <summary>Aligns the PCM output to the video's origin (<c>-af</c>).</summary>
    public const string AudioFilter = "aresample=async=1:first_pts=0";

    /// <summary>The <c>-vf</c> chain: encoder scaler with forced aspect, plus the overlay stage when on.</summary>
    public static string VideoFilter(FfmpegCommandOptions o)
    {
        var vf = FfmpegFilters.WithAspect(o.Encoder.Vf, o.Aspect);
        if (o.OverlayFile is not { } file)
        {
            return vf;
        }

        var text = DrawText(file, o.OverlayFontSize);
        // drawtext needs software frames: after the GPU scaler, download, draw, and upload again.
        return o.Encoder.Name == EncoderConfig.Vaapi
            ? $"{vf},hwdownload,format=nv12,{text},format=nv12,hwupload"
            : $"{vf},{text}";
    }

    /// <summary>Monospace, top-left, on a semi-opaque box; <c>expansion=none</c> so '%' is literal.</summary>
    public static string DrawText(string file, int fontSize) =>
        $"drawtext=textfile={Escape(file)}:reload=1:expansion=none:font=monospace:fontsize={fontSize.ToString(CultureInfo.InvariantCulture)}"
        + ":fontcolor=white:box=1:boxcolor=black@0.6:boxborderw=8:line_spacing=4:x=12:y=12";

    /// <summary>Two-level filtergraph escaping: option value, then the filter graph.</summary>
    internal static string Escape(string value) =>
        EscapeChars(EscapeChars(value, @"\':"), @"\'[],;");

    private static string EscapeChars(string value, string special)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (special.Contains(c))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}

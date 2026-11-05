using System.ComponentModel;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Slopsoil.Streaming.Process;

namespace Slopsoil.Streaming.Video;

/// <summary>
/// A still image as a short looping H.264 clip (spec 015): the loading card while the read-ahead fills and the pause
/// card while paused. Every keyframe carries SPS/PPS so the loop (and a switch to it) always decodes.
/// </summary>
public sealed class StillClip
{
    internal const int NalIdr = 5;
    internal const int NalSps = 7;
    internal const int NalPps = 8;

    public StillClip(IReadOnlyList<IReadOnlyList<byte[]>> frames)
    {
        if (frames.Count == 0 || !IsKeyframe(frames[0]))
        {
            throw new ArgumentException("a still clip starts with a keyframe", nameof(frames));
        }

        Frames = frames;
    }

    /// <summary>Access units (NALs without start codes), the first a keyframe with SPS/PPS.</summary>
    public IReadOnlyList<IReadOnlyList<byte[]>> Frames { get; }

    internal static int NalType(byte[] nal) => nal.Length == 0 ? -1 : nal[0] & 0x1F;

    internal static bool IsKeyframe(IReadOnlyList<byte[]> frame) => frame.Any(n => NalType(n) == NalIdr);

    internal static bool HasSps(IReadOnlyList<byte[]> frame) => frame.Any(n => NalType(n) == NalSps);

    /// <summary>
    /// Build a clip from encoder output: keyframes without SPS/PPS get the stream's first SPS/PPS prepended; frames
    /// before the first keyframe are dropped. Null when there is no keyframe.
    /// </summary>
    public static StillClip? FromAccessUnits(IReadOnlyList<IReadOnlyList<byte[]>> units)
    {
        var parameterSets = units.SelectMany(u => u).Where(n => NalType(n) is NalSps or NalPps).ToList();
        var sps = parameterSets.FirstOrDefault(n => NalType(n) == NalSps);
        var pps = parameterSets.FirstOrDefault(n => NalType(n) == NalPps);
        var frames = new List<IReadOnlyList<byte[]>>();
        foreach (var unit in units)
        {
            if (frames.Count == 0 && !IsKeyframe(unit))
            {
                continue;
            }

            if (IsKeyframe(unit) && !HasSps(unit) && sps is not null && pps is not null)
            {
                frames.Add([sps, pps, .. unit]);
            }
            else
            {
                frames.Add(unit);
            }
        }

        return frames.Count == 0 ? null : new StillClip(frames);
    }

    /// <summary>
    /// Encode <paramref name="imagePath"/> with <paramref name="encoder"/> (its box, bitrate and filters) as a
    /// <see cref="LoopSeconds"/> s clip at <paramref name="fps"/>, a keyframe every second. Null when FFmpeg fails.
    /// </summary>
    public static StillClip? Encode(
        IProcessRunner processes, string imagePath, EncoderConfig encoder, double fps, string tempDirectory, ILogger? logger = null, string executable = "ffmpeg")
    {
        var log = logger ?? NullLogger.Instance;
        var output = Path.Combine(tempDirectory, $"slopsoil_still_{Guid.NewGuid():N}.h264");
        var gop = Math.Max(1, (int)Math.Round(fps)).ToString(CultureInfo.InvariantCulture);
        List<string> args =
        [
            executable, "-hide_banner", "-loglevel", "error", "-y",
            .. encoder.PreInput,
            "-loop", "1", "-i", imagePath, "-t", LoopSeconds.ToString(CultureInfo.InvariantCulture),
            "-vf", StillFilter(encoder), "-c:v", encoder.Name,
            .. encoder.PostCodec,
            "-fps_mode", "cfr", "-r", FrameRate.ToFfmpeg(fps), "-g", gop, "-an", "-f", "h264", output,
        ];
        try
        {
            var result = processes.Run(args, EncodeTimeout);
            if (result.ExitCode != 0 || !File.Exists(output))
            {
                log.LogWarning("could not encode the still image {Image} with {Encoder} (exit {Code}): {Error}", imagePath, encoder.Name, result.ExitCode, result.StandardError.Trim());
                return null;
            }

            var splitter = new AccessUnitSplitter();
            var bytes = File.ReadAllBytes(output);
            var units = splitter.Feed(bytes).Concat(splitter.Flush()).Where(u => u.Count > 0).ToList();
            var clip = FromAccessUnits(units);
            if (clip is null)
            {
                log.LogWarning("the still image {Image} encoded to no keyframe with {Encoder}", imagePath, encoder.Name);
            }
            else
            {
                log.LogInformation("still image {Image} ready: {Frames} frames at {Fps} fps with {Encoder}", imagePath, clip.Frames.Count, FrameRate.Format(fps), encoder.Name);
            }

            return clip;
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or IOException)
        {
            log.LogWarning("could not encode the still image {Image}: {Error}", imagePath, ex.Message);
            return null;
        }
        finally
        {
            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The encoder's scaler, ending in 4:2:0: images are often 4:4:4 / RGB (PNG, some JPEGs), which the software
    /// encoders' profiles reject. The VA-API chain already uploads as NV12.
    /// </summary>
    internal static string StillFilter(EncoderConfig encoder) =>
        encoder.Vf.Contains("hwupload", StringComparison.Ordinal) ? encoder.Vf : encoder.Vf + ",format=yuv420p";

    /// <summary>Loop length; the clip holds one keyframe per second.</summary>
    public const int LoopSeconds = 2;

    public static readonly TimeSpan EncodeTimeout = TimeSpan.FromSeconds(30);
}

/// <summary>A playback position in a <see cref="StillClip"/> (one per player; the clip itself is shared).</summary>
public sealed class StillCursor
{
    private StillClip? _clip;
    private int _next;

    /// <summary>The next frame of <paramref name="clip"/>; a different clip (or <see cref="Reset"/>) starts at its keyframe.</summary>
    public IReadOnlyList<byte[]> Next(StillClip clip)
    {
        if (!ReferenceEquals(clip, _clip))
        {
            _clip = clip;
            _next = 0;
        }

        var frame = clip.Frames[_next];
        _next = (_next + 1) % clip.Frames.Count;
        return frame;
    }

    public void Reset() => _clip = null;
}

/// <summary>
/// The loading and pause images (spec 015), encoded once per encoder setup and frame rate, in the background.
/// </summary>
public sealed class StillImages(string? loadingImage, string? pausedImage, IProcessRunner? processes = null, ILogger? logger = null)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<StillClip?>>> _clips = new();
    private readonly IProcessRunner _processes = processes ?? SystemProcessRunner.Instance;

    public string? LoadingImage { get; } = loadingImage;

    public string? PausedImage { get; } = pausedImage;

    /// <summary>Directory for the temporary encoder output.</summary>
    public string TempDirectory { get; init; } = Path.GetTempPath();

    /// <summary>A getter for the loading clip: null until it is encoded (or with no image configured).</summary>
    public Func<StillClip?>? Loading(EncoderConfig encoder, double fps) => Getter(LoadingImage, encoder, fps);

    /// <summary>A getter for the pause clip: null until it is encoded (or with no image configured).</summary>
    public Func<StillClip?>? Paused(EncoderConfig encoder, double fps) => Getter(PausedImage, encoder, fps);

    private Func<StillClip?>? Getter(string? image, EncoderConfig encoder, double fps)
    {
        if (image is null)
        {
            return null;
        }

        var key = string.Join('|', image, encoder.Name, encoder.Vf, string.Join(' ', encoder.PostCodec), FrameRate.ToFfmpeg(fps));
        var task = _clips.GetOrAdd(key, _ => new Lazy<Task<StillClip?>>(() => Task.Run(() => StillClip.Encode(_processes, image, encoder, fps, TempDirectory, logger)))).Value;
        return () => task.IsCompletedSuccessfully ? task.Result : null;
    }
}

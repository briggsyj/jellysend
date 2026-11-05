using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using JellySend.Streaming.Process;

namespace JellySend.Streaming.Video;

/// <summary>
/// The decoders the bot's FFmpeg has (<c>ffmpeg -decoders</c>), by codec. Fedora's <c>ffmpeg-free</c> has no HEVC or
/// E-AC-3 decoder and decodes H.264 only through OpenH264 (8-bit, progressive), so a direct stream of such a file
/// cannot play; Jellyfin's transcode is used instead (spec 011).
/// </summary>
public sealed partial class DecoderSet
{
    private readonly Dictionary<string, List<string>> _byCodec;

    private DecoderSet(Dictionary<string, List<string>> byCodec) => _byCodec = byCodec;

    /// <summary>Codecs with at least one decoder, e.g. <c>h264</c>, <c>ac3</c>, <c>dts</c>.</summary>
    public IReadOnlyCollection<string> Codecs => _byCodec.Keys;

    /// <summary>Run <c>ffmpeg -hide_banner -decoders</c>; null when FFmpeg is missing or the probe fails.</summary>
    public static DecoderSet? Detect(IProcessRunner processes, ILogger? logger = null, string executable = "ffmpeg")
    {
        var log = logger ?? NullLogger.Instance;
        try
        {
            var result = processes.Run([executable, "-hide_banner", "-decoders"], EncoderDetector.ProbeTimeout);
            var set = Parse(result.StandardOutput);
            if (set.Codecs.Count == 0)
            {
                log.LogWarning("ffmpeg -decoders listed nothing; direct streams are not checked");
                return null;
            }

            log.LogInformation(
                "ffmpeg decoders: hevc {Hevc}, h264 {H264}, eac3 {Eac3}",
                set.Describe("hevc"), set.Describe("h264"), set.Describe("eac3"));
            return set;
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException)
        {
            log.LogWarning("could not list ffmpeg decoders ({Error}); direct streams are not checked", ex.Message);
            return null;
        }
    }

    /// <summary>Parse <c>ffmpeg -decoders</c> output: lines <c> V....D name  description (codec id)</c>.</summary>
    public static DecoderSet Parse(string output)
    {
        var byCodec = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var m = DecoderLine().Match(line);
            if (!m.Success || m.Groups["name"].Value == "=")
            {
                continue;
            }

            var name = m.Groups["name"].Value;
            var codec = CodecId().Match(m.Groups["rest"].Value) is { Success: true } c ? c.Groups[1].Value : name;
            if (!byCodec.TryGetValue(codec, out var names))
            {
                byCodec[codec] = names = [];
            }

            names.Add(name);
        }

        return new DecoderSet(byCodec);
    }

    /// <summary>
    /// Whether a video stream can be decoded. Unknown codec (null) → true. H.264 decoded only by OpenH264 also
    /// needs 8-bit, progressive video (no High 10 / interlaced support).
    /// </summary>
    public bool CanDecodeVideo(string? codec, int? bitDepth = null, bool interlaced = false)
    {
        if (codec is null)
        {
            return true;
        }

        if (!_byCodec.TryGetValue(codec, out var names))
        {
            return false;
        }

        var openH264Only = codec.Equals("h264", StringComparison.OrdinalIgnoreCase) && names.TrueForAll(n => n == "libopenh264");
        return !openH264Only || (bitDepth is null or <= 8 && !interlaced);
    }

    /// <summary>Whether an audio stream can be decoded; no audio (null) → true.</summary>
    public bool CanDecodeAudio(string? codec) => codec is null || _byCodec.ContainsKey(codec);

    private string Describe(string codec) => _byCodec.TryGetValue(codec, out var names) ? string.Join("/", names) : "none";

    [GeneratedRegex(@"^\s*[VAS][.FSXBD]{5}\s+(?<name>\S+)\s*(?<rest>.*)$")]
    private static partial Regex DecoderLine();

    [GeneratedRegex(@"\(codec (\S+?)\)\s*$")]
    private static partial Regex CodecId();
}

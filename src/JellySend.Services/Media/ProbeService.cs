using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using JellySend.Common.Logging;

namespace JellySend.Services.Media;

/// <summary>Summary of a stream's first video stream plus audio presence (the dict <c>probe_stream</c> returns).</summary>
/// <param name="Codec">Video codec name (<c>codec_name</c>, "unknown" if missing).</param>
/// <param name="Fps">Frame rate from <c>r_frame_rate</c>, rounded to 3 decimals (25 when unparsable).</param>
/// <param name="Profile">Codec profile ("unknown" if missing).</param>
/// <param name="HasBFrames">Whether the stream reports B-frames.</param>
/// <param name="Width">Width in pixels (0 if missing).</param>
/// <param name="Height">Height in pixels (0 if missing).</param>
/// <param name="HasAudio">Whether any audio stream is present.</param>
/// <param name="AudioCodec">First audio stream's codec, if any.</param>
public sealed record ProbeResult(string Codec, double Fps, string Profile, bool HasBFrames, int Width, int Height, bool HasAudio, string? AudioCodec);

/// <summary>Stream probing via ffprobe (port of <c>services/probe.py</c>); URLs and stderr are logged redacted.</summary>
public sealed class ProbeService
{
    /// <summary>ffprobe wall-clock limit.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _runner;
    private readonly ILogger _log;
    private readonly string _ffprobe;

    /// <summary>Create a probe service.</summary>
    /// <param name="runner">Process seam; <see cref="SystemProcessRunner"/> when null.</param>
    /// <param name="logger">Logger; <see cref="NullLogger"/> when null.</param>
    /// <param name="ffprobePath">ffprobe executable.</param>
    public ProbeService(IProcessRunner? runner = null, ILogger? logger = null, string ffprobePath = "ffprobe")
    {
        _runner = runner ?? SystemProcessRunner.Instance;
        _log = logger ?? NullLogger.Instance;
        _ffprobe = ffprobePath;
    }

    /// <summary>
    /// ffprobe arguments: all streams (no <c>-select_streams</c>) and a 10 MB / 10 s window so HLS audio renditions
    /// are found (a 2 MB window often returns only video).
    /// </summary>
    public static IReadOnlyList<string> Arguments(string url) =>
    [
        "-v", "warning",
        "-show_streams",
        "-print_format", "json",
        "-probesize", "10000000",
        "-analyzeduration", "10000000",
        url,
    ];

    /// <summary>Probe <paramref name="url"/>; null when ffprobe fails, times out, or finds no video stream (logged).</summary>
    public async Task<ProbeResult?> ProbeStreamAsync(string url, CancellationToken ct = default)
    {
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_ffprobe, Arguments(url), Timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("ffprobe timed out for {Url}", Redactor.RedactUrl(url));
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("ffprobe error for {Url}: {Error}", Redactor.RedactUrl(url), Redactor.RedactText(ex.Message));
            return null;
        }

        if (result.ExitCode != 0)
        {
            _log.LogWarning("ffprobe failed (exit {ExitCode}) for {Url}", result.ExitCode, Redactor.RedactUrl(url));
            foreach (var line in result.StdErr.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                _log.LogWarning("ffprobe stderr: {Line}", Redactor.RedactText(line));
            }

            return null;
        }

        return Parse(result.StdOut);
    }

    /// <summary>Parse ffprobe <c>-show_streams</c> JSON; null when empty, malformed, or without a video stream.</summary>
    internal static ProbeResult? Parse(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("streams", out var streams)
            || streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var all = streams.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object).ToList();
        var video = all.Where(s => Str(s, "codec_type") == "video").ToList();
        var audio = all.Where(s => Str(s, "codec_type") == "audio").ToList();
        if (video.Count == 0)
        {
            return null;
        }

        var v = video[0];
        return new ProbeResult(
            Str(v, "codec_name") ?? "unknown",
            Math.Round(ParseFps(Str(v, "r_frame_rate") ?? "25/1"), 3),
            Str(v, "profile") ?? "unknown",
            Int(v, "has_b_frames") != 0,
            Int(v, "width"),
            Int(v, "height"),
            audio.Count > 0,
            audio.Count > 0 ? Str(audio[0], "codec_name") : null);
    }

    /// <summary><c>num/den</c> → fps; 25 when malformed or the denominator is not positive.</summary>
    internal static double ParseFps(string rate)
    {
        var parts = rate.Split('/');
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den > 0
                ? num / den
                : 25.0;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}

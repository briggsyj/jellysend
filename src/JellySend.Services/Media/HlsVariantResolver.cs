using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using JellySend.Common.Logging;

namespace JellySend.Services.Media;

/// <summary>
/// HLS master-playlist resolution (port of <c>services/hls.py</c>): if a URL is a master playlist, use its
/// highest-bandwidth variant so FFmpeg bypasses separate audio rendition groups (which some providers serve
/// unreliably) and uses the audio muxed into the variant's segments.
/// </summary>
public sealed partial class HlsVariantResolver
{
    /// <summary>Bytes read to detect HLS markers (avoids hanging on endless bodies such as MJPEG).</summary>
    public const int MaxPeekBytes = 8192;

    /// <summary>Request timeout.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    /// <summary>Create a resolver.</summary>
    /// <param name="http">HTTP client (tests inject a fake handler); a shared default when null.</param>
    /// <param name="logger">Logger; <see cref="NullLogger"/> when null.</param>
    /// <param name="time">Clock used for the request timeout.</param>
    public HlsVariantResolver(HttpClient? http = null, ILogger? logger = null, TimeProvider? time = null)
    {
        _http = http ?? DefaultHttp.Value;
        _log = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    private static Lazy<HttpClient> DefaultHttp { get; } = new(() => new HttpClient());

    /// <summary>
    /// Return the highest-bandwidth variant URL when <paramref name="masterUrl"/> is an HLS master playlist,
    /// otherwise (variant playlist, not HLS, or any fetch error) the URL unchanged.
    /// </summary>
    public async Task<string> ExtractVariantUrlAsync(string masterUrl, CancellationToken ct = default)
    {
        string text;
        try
        {
            text = await PeekAsync(masterUrl, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return masterUrl;
        }

        var best = PickVariant(masterUrl, text);
        if (best is not null && best != masterUrl)
        {
            _log.LogInformation("HLS master → variant: {Url}", Redactor.RedactUrl(best));
            return best;
        }

        return masterUrl;
    }

    /// <summary>
    /// The highest-<c>BANDWIDTH</c> <c>#EXT-X-STREAM-INF</c> URI in a master playlist (relative URIs resolved by
    /// appending to the master URL's directory), or null when <paramref name="text"/> is not a master playlist.
    /// First variant wins ties; a missing BANDWIDTH counts as 0.
    /// </summary>
    internal static string? PickVariant(string masterUrl, string text)
    {
        if (!text.Contains("#EXTM3U", StringComparison.Ordinal) || !text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
        {
            return null;
        }

        var slash = masterUrl.LastIndexOf('/');
        var baseUrl = (slash >= 0 ? masterUrl[..slash] : masterUrl) + "/";
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        long bestBandwidth = -1;
        string? best = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) || i + 1 >= lines.Length)
            {
                continue;
            }

            var m = Bandwidth().Match(line);
            var bandwidth = m.Success && long.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var bw) ? bw : 0;
            var next = lines[i + 1].Trim();
            if (next.Length == 0 || next.StartsWith('#'))
            {
                continue;
            }

            var isAbsolute = next.StartsWith("http://", StringComparison.Ordinal) || next.StartsWith("https://", StringComparison.Ordinal);
            if (bandwidth > bestBandwidth)
            {
                bestBandwidth = bandwidth;
                best = isAbsolute ? next : baseUrl + next;
            }
        }

        return best;
    }

    [GeneratedRegex(@"BANDWIDTH=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Bandwidth();

    private async Task<string> PeekAsync(string url, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "jellysend/1.0");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        var buffer = new byte[MaxPeekBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(total), linked.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}

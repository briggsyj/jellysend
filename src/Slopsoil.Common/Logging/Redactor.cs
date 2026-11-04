using System.Text.RegularExpressions;

namespace Slopsoil.Common.Logging;

/// <summary>Scrubs credentials from URLs and free text before logging. Port of <c>redact.py</c>.</summary>
public static partial class Redactor
{
    /// <summary>Query parameters that carry credentials (case-insensitive).</summary>
    public static readonly IReadOnlySet<string> SecretQueryKeys =
        new HashSet<string>(["api_key", "apikey", "token", "access_token", "password", "x-emby-token"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Drop <c>user:pass@</c> and secret query parameters from a URL; non-URLs go through <see cref="RedactText"/>.</summary>
    public static string RedactUrl(string url)
    {
        // Only explicit scheme://… strings are parsed: on Unix a bare path is also an "absolute URI" (file://), and
        // converting it would escape '?' into the path where the secret would survive.
        if (!url.Contains("://", StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return RedactText(url);
        }

        var query = uri.Query.TrimStart('?');
        var kept = query.Length == 0
            ? []
            : query.Split('&').Where(p => !SecretQueryKeys.Contains(Uri.UnescapeDataString(p.Split('=', 2)[0]))).ToArray();
        // Unlike Python, an explicit default port (":80") is dropped — harmless for logs.
        // RedactText as a second pass: anything the URI parser normalised oddly is still masked.
        return RedactText(new UriBuilder(uri) { UserName = "", Password = "", Query = string.Join('&', kept) }.Uri.ToString());
    }

    /// <summary>Mask secrets in any text that may embed URLs (e.g. FFmpeg stderr, exception messages).</summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        text = SecretParam().Replace(text, "$1***");
        return UserInfo().Replace(text, "$1***@");
    }

    [GeneratedRegex("""([?&;](?:access_token|api_key|apikey|password|token|x-emby-token)=)[^&\s"'<>]+""", RegexOptions.IgnoreCase)]
    private static partial Regex SecretParam();

    [GeneratedRegex(@"(\b[a-z][a-z0-9+.-]*://)[^/\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UserInfo();
}

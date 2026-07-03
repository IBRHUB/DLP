using System.Diagnostics;
using System.Net;

internal sealed record YtDlpDownloadAttempt(
    string Name,
    string? ExtractorArgs,
    string? VideoFormatSelector = null,
    bool SuppressCookies = false,
    bool AllowPlaylist = false)
{
    public static YtDlpDownloadAttempt Default { get; } = new("default", null);

    public void AddTo(ProcessStartInfo startInfo)
    {
        if (string.IsNullOrWhiteSpace(ExtractorArgs))
        {
            return;
        }

        startInfo.ArgumentList.Add("--extractor-args");
        startInfo.ArgumentList.Add(ExtractorArgs);
    }

    public string GetVideoFormatSelector(string defaultSelector)
    {
        return string.IsNullOrWhiteSpace(VideoFormatSelector)
            ? defaultSelector
            : VideoFormatSelector;
    }
}

internal static class YtDlpSites
{
    private static readonly string[] DirectMediaFallbackExtensions =
    [
        ".mp4",
        ".webm",
        ".m4v",
        ".mov"
    ];

    public static bool IsAllowedHost(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();

        return YouTube.IsHost(host)
            || TikTok.IsHost(host)
            || Instagram.IsHost(host)
            || X.IsHost(host)
            || SoundCloud.IsHost(host);
    }

    public static bool ShouldPreferYtDlp(string? url, string? referer)
    {
        return IsSupportedMediaPageUrl(url) || IsSupportedMediaPageUrl(referer);
    }

    public static IReadOnlyList<YtDlpDownloadAttempt> GetDownloadAttempts(
        string url,
        string? referer,
        string? cookieBrowser)
    {
        Uri? uri = TryCreateHttpsUri(url) ?? TryCreateHttpsUri(referer);
        bool hasCookies = CookieBrowserCatalog.Normalize(cookieBrowser) is not null;

        if (uri is null)
        {
            return [YtDlpDownloadAttempt.Default];
        }

        string host = uri.Host.ToLowerInvariant();

        if (X.IsHost(host))
        {
            return X.GetDownloadAttempts(hasCookies);
        }

        if (Instagram.IsHost(host))
        {
            return Instagram.GetDownloadAttempts(hasCookies);
        }

        if (YouTube.IsHost(host))
        {
            return YouTube.GetDownloadAttempts(hasCookies);
        }

        return [YtDlpDownloadAttempt.Default];
    }

    public static string DetectPlatform(string? url, string? referer)
    {
        Uri? uri = TryCreateHttpsUri(url) ?? TryCreateHttpsUri(referer);

        if (uri is null)
        {
            return "unknown";
        }

        string host = uri.Host.ToLowerInvariant();

        if (Instagram.IsHost(host))
        {
            return Instagram.DisplayName;
        }

        if (X.IsHost(host))
        {
            return X.DisplayName;
        }

        if (TikTok.IsHost(host))
        {
            return TikTok.DisplayName;
        }

        if (YouTube.IsHost(host))
        {
            return YouTube.DisplayName;
        }

        if (SoundCloud.IsHost(host))
        {
            return SoundCloud.DisplayName;
        }

        return "unknown";
    }

    public static string ClassifyFailure(string text)
    {
        return Instagram.ClassifyFailure(text)
            ?? X.ClassifyFailure(text)
            ?? YouTube.ClassifyFailure(text)
            ?? ClassifyGenericFailure(text);
    }

    public static bool LooksLikeDirectMediaUrl(Uri uri)
    {
        string path = WebUtility.UrlDecode(uri.AbsolutePath).TrimEnd('/');
        string extension = Path.GetExtension(path);

        if (DirectMediaFallbackExtensions.Any(mediaExtension =>
            string.Equals(extension, mediaExtension, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (string parameterName in new[] { "file", "filename", "name", "src", "url" })
        {
            string? value = GetQueryValue(uri, parameterName);

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string fileName = Path.GetFileName(value.TrimEnd('/'));

            if (DirectMediaFallbackExtensions.Any(mediaExtension =>
                string.Equals(Path.GetExtension(fileName), mediaExtension, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HostMatches(string host, params string[] domains)
    {
        return domains.Any(domain =>
            string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasQueryParameter(Uri uri, string name)
    {
        string query = uri.Query;

        if (query.StartsWith("?", StringComparison.Ordinal))
        {
            query = query[1..];
        }

        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separatorIndex = part.IndexOf('=', StringComparison.Ordinal);
            string key = separatorIndex >= 0 ? part[..separatorIndex] : part;

            if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSupportedMediaPageUrl(string? url)
    {
        Uri? uri = TryCreateHttpsUri(url);

        if (uri is null)
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();

        return YouTube.IsMediaPage(uri, host)
            || TikTok.IsMediaPage(uri, host)
            || Instagram.IsMediaPage(uri, host)
            || X.IsMediaPage(uri, host)
            || SoundCloud.IsMediaPage(uri, host);
    }

    private static Uri? TryCreateHttpsUri(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }

    private static string? GetQueryValue(Uri uri, string name)
    {
        string query = uri.Query;

        if (query.StartsWith("?", StringComparison.Ordinal))
        {
            query = query[1..];
        }

        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separatorIndex = part.IndexOf('=', StringComparison.Ordinal);

            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = Uri.UnescapeDataString(part[..separatorIndex].Replace("+", " ", StringComparison.Ordinal));

            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Uri.UnescapeDataString(part[(separatorIndex + 1)..].Replace("+", " ", StringComparison.Ordinal));
        }

        return null;
    }

    private static string ClassifyGenericFailure(string text)
    {
        if (text.Contains("requiring login", StringComparison.Ordinal)
            || text.Contains("requires login", StringComparison.Ordinal)
            || text.Contains("login for access", StringComparison.Ordinal)
            || text.Contains("use --cookies-from-browser", StringComparison.Ordinal))
        {
            return "login_required";
        }

        if (text.Contains("could not copy chrome cookie database", StringComparison.Ordinal))
        {
            return "browser_cookie_database_locked";
        }

        if (text.Contains("unsupported url", StringComparison.Ordinal))
        {
            return "unsupported_url";
        }

        if (text.Contains("private", StringComparison.Ordinal))
        {
            return "private_content";
        }

        if (text.Contains("403", StringComparison.Ordinal) || text.Contains("forbidden", StringComparison.Ordinal))
        {
            return "http_forbidden";
        }

        if (text.Contains("404", StringComparison.Ordinal) || text.Contains("not found", StringComparison.Ordinal))
        {
            return "http_not_found";
        }

        if (text.Contains("not available", StringComparison.Ordinal))
        {
            return "content_unavailable";
        }

        return "unknown_yt_dlp_failure";
    }
}

internal static class YtDlpPlatformPolicy
{
    public static bool ShouldPreferYtDlp(string url, string? referer)
    {
        return YtDlpSites.ShouldPreferYtDlp(url, referer);
    }
}

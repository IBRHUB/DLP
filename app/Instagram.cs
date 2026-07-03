using System.Text.RegularExpressions;

internal static class Instagram
{
    public const string DisplayName = "Instagram";
    private const string DirectMp4FormatSelector = "b[ext=mp4][protocol^=http]/b[protocol^=http]/b[ext=mp4]/bv*+ba/best";
    private const string AnyFormatSelector = "bv*+ba/bestaudio/best";

    private static readonly string[] Hosts =
    [
        "instagram.com",
        "www.instagram.com",
        "m.instagram.com"
    ];

    public static bool IsHost(string host) =>
        YtDlpSites.HostMatches(host, Hosts) || IsCdnHost(host);

    public static bool IsDirectMediaUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && IsCdnHost(uri.Host)
            && !YtDlpSites.HasQueryParameter(uri, "bytestart")
            && !YtDlpSites.HasQueryParameter(uri, "byteend")
            && YtDlpSites.LooksLikeDirectMediaUrl(uri);
    }

    public static bool IsMediaPage(Uri uri, string host)
    {
        if (!IsHost(host))
        {
            return false;
        }

        string path = uri.AbsolutePath.ToLowerInvariant();

        if (IsCdnHost(host))
        {
            return Regex.IsMatch(path, @"\.(?:mp4|m4v|mov|webm|m3u8|m3u|mpd)(?:$|[?#])", RegexOptions.IgnoreCase)
                || YtDlpSites.LooksLikeDirectMediaUrl(uri);
        }

        return Regex.IsMatch(path, @"^/(?:[^/]+/)?(?:p|tv|reels?)/(?!audio/)[^/?#&]+(?:/(?:embed)?)?(?:$|/)?", RegexOptions.IgnoreCase)
            || Regex.IsMatch(path, @"^/stories/(?:highlights/\d+|[^/?#]+(?:/\d+)?)(?:$|/)?", RegexOptions.IgnoreCase);
    }

    public static IReadOnlyList<YtDlpDownloadAttempt> GetDownloadAttempts(bool hasCookies)
    {
        if (hasCookies)
        {
            return
            [
                new("instagram-auth-web-mp4", "instagram:app_id=web", DirectMp4FormatSelector, AllowPlaylist: true),
                new("instagram-auth-ios-mp4", "instagram:app_id=ios", DirectMp4FormatSelector, AllowPlaylist: true),
                new("instagram-auth-web-any", "instagram:app_id=web", AnyFormatSelector, AllowPlaylist: true),
                new("instagram-guest-web-mp4", "instagram:app_id=web", DirectMp4FormatSelector, SuppressCookies: true, AllowPlaylist: true),
                new("instagram-guest-ios-mp4", "instagram:app_id=ios", DirectMp4FormatSelector, SuppressCookies: true, AllowPlaylist: true),
                new("instagram-guest-ios-any", "instagram:app_id=ios", AnyFormatSelector, SuppressCookies: true, AllowPlaylist: true)
            ];
        }

        return
        [
            new("instagram-web-mp4", "instagram:app_id=web", DirectMp4FormatSelector, AllowPlaylist: true),
            new("instagram-ios-mp4", "instagram:app_id=ios", DirectMp4FormatSelector, AllowPlaylist: true),
            new("instagram-web-any", "instagram:app_id=web", AnyFormatSelector, AllowPlaylist: true),
            new("instagram-ios-any", "instagram:app_id=ios", AnyFormatSelector, AllowPlaylist: true)
        ];
    }

    public static string? ClassifyFailure(string text)
    {
        if (text.Contains("this content isn't available to everyone", StringComparison.Ordinal))
        {
            return "instagram_restricted_audience";
        }

        if (text.Contains("instagram sent an empty media response", StringComparison.Ordinal))
        {
            return "instagram_empty_media_response";
        }

        if (text.Contains("there is no video in this post", StringComparison.Ordinal))
        {
            return "instagram_no_video";
        }

        if (text.Contains("unable to extract user id", StringComparison.Ordinal)
            || text.Contains("unable to extract data", StringComparison.Ordinal))
        {
            return "instagram_extraction_failed";
        }

        if (text.Contains("no csrf token set by instagram api", StringComparison.Ordinal))
        {
            return "instagram_missing_csrf_token";
        }

        if (text.Contains("instagram api is not granting access", StringComparison.Ordinal))
        {
            return "instagram_api_access_denied";
        }

        if (text.Contains("the webpage request was redirected to the login page", StringComparison.Ordinal)
            || text.Contains("only available for registered users", StringComparison.Ordinal))
        {
            return "instagram_login_required";
        }

        return null;
    }

    private static bool IsCdnHost(string host)
    {
        return string.Equals(host, "cdninstagram.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".cdninstagram.com", StringComparison.OrdinalIgnoreCase)
            || (host.Contains("instagram", StringComparison.OrdinalIgnoreCase)
                && host.EndsWith(".fbcdn.net", StringComparison.OrdinalIgnoreCase));
    }
}

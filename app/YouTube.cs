internal static class YouTube
{
    public const string DisplayName = "YouTube";

    private static readonly string[] Hosts =
    [
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "youtu.be"
    ];

    public static bool IsHost(string host) => YtDlpSites.HostMatches(host, Hosts);

    public static bool IsMediaPage(Uri uri, string host)
    {
        if (!IsHost(host))
        {
            return false;
        }

        string path = uri.AbsolutePath.ToLowerInvariant();

        if (host == "youtu.be")
        {
            return path.Length > 1;
        }

        return (path == "/watch" && YtDlpSites.HasQueryParameter(uri, "v"))
            || path.StartsWith("/shorts/", StringComparison.Ordinal)
            || path.StartsWith("/live/", StringComparison.Ordinal)
            || path.StartsWith("/clip/", StringComparison.Ordinal);
    }

    public static IReadOnlyList<YtDlpDownloadAttempt> GetDownloadAttempts(bool hasCookies)
    {
        return
        [
            YtDlpDownloadAttempt.Default,
            new("youtube-tv-mweb", "youtube:player_client=tv,web_safari,mweb"),
            new("youtube-android-incomplete", "youtube:player_client=android_vr,web_safari;formats=incomplete")
        ];
    }

    public static string? ClassifyFailure(string text)
    {
        if (text.Contains("sign in to confirm", StringComparison.Ordinal)
            || text.Contains("confirm your age", StringComparison.Ordinal)
            || text.Contains("this video may be inappropriate", StringComparison.Ordinal))
        {
            return "youtube_login_or_age_required";
        }

        if (text.Contains("nsig extraction failed", StringComparison.Ordinal))
        {
            return "youtube_nsig_extraction_failed";
        }

        if (text.Contains("requested format is not available", StringComparison.Ordinal))
        {
            return "youtube_format_unavailable";
        }

        if (text.Contains("po token", StringComparison.Ordinal)
            || text.Contains("proof of origin", StringComparison.Ordinal))
        {
            return "youtube_po_token_required";
        }

        return null;
    }
}

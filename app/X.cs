using System.Text.RegularExpressions;

internal static class X
{
    public const string DisplayName = "Twitter/X";
    private const string HttpMp4FormatSelector = "b[ext=mp4][protocol^=http]/b[protocol^=http]/bv*+ba/bestaudio/best";
    private const string AnyFormatSelector = "bv*+ba/bestaudio/best";

    private static readonly string[] Hosts =
    [
        "x.com",
        "www.x.com",
        "mobile.x.com",
        "twitter.com",
        "www.twitter.com",
        "mobile.twitter.com",
        "video.twimg.com"
    ];

    public static bool IsHost(string host) => YtDlpSites.HostMatches(host, Hosts);

    public static bool IsMediaPage(Uri uri, string host)
    {
        if (!IsHost(host))
        {
            return false;
        }

        string path = uri.AbsolutePath.ToLowerInvariant();

        if (string.Equals(host, "video.twimg.com", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(path, @"\.(?:mp4|m3u8|m3u|mov|m4v)(?:$|[?#])", RegexOptions.IgnoreCase)
                || path.Contains("/amplify_video/", StringComparison.Ordinal)
                || path.Contains("/ext_tw_video/", StringComparison.Ordinal)
                || path.Contains("/tweet_video/", StringComparison.Ordinal);
        }

        return Regex.IsMatch(path, @"^/(?:i/web/status/\d+|[^/]+/status/\d+|statuses/\d+)(?:/(?:video|photo)/\d+)?(?:$|/)?", RegexOptions.IgnoreCase)
            || Regex.IsMatch(path, @"^/i/(?:cards/tfw/v1|videos(?:/tweet)?)/\d+", RegexOptions.IgnoreCase)
            || path.StartsWith("/i/broadcasts/", StringComparison.Ordinal)
            || path.StartsWith("/i/spaces/", StringComparison.Ordinal);
    }

    public static IReadOnlyList<YtDlpDownloadAttempt> GetDownloadAttempts(bool hasCookies)
    {
        if (hasCookies)
        {
            return
            [
                new("x-authenticated-http-mp4", null, HttpMp4FormatSelector),
                new("x-authenticated-any", null, AnyFormatSelector),
                new("x-guest-graphql-http-mp4", "twitter:api=graphql", HttpMp4FormatSelector, SuppressCookies: true),
                new("x-guest-legacy-http-mp4", "twitter:api=legacy", HttpMp4FormatSelector, SuppressCookies: true),
                new("x-guest-syndication-http-mp4", "twitter:api=syndication", HttpMp4FormatSelector, SuppressCookies: true),
                new("x-guest-syndication-any", "twitter:api=syndication", AnyFormatSelector, SuppressCookies: true)
            ];
        }

        return
        [
            new("x-graphql-http-mp4", "twitter:api=graphql", HttpMp4FormatSelector),
            new("x-graphql-any", "twitter:api=graphql", AnyFormatSelector),
            new("x-legacy-http-mp4", "twitter:api=legacy", HttpMp4FormatSelector),
            new("x-legacy-any", "twitter:api=legacy", AnyFormatSelector),
            new("x-syndication-http-mp4", "twitter:api=syndication", HttpMp4FormatSelector),
            new("x-syndication-any", "twitter:api=syndication", AnyFormatSelector)
        ];
    }

    public static string? ClassifyFailure(string text)
    {
        if (text.Contains("no video could be found in this tweet", StringComparison.Ordinal))
        {
            return "twitter_no_video";
        }

        if (text.Contains("error(s) while querying api", StringComparison.Ordinal)
            || text.Contains("failed to parse json", StringComparison.Ordinal)
            || text.Contains("could not retrieve guest token", StringComparison.Ordinal))
        {
            return "twitter_api_failure";
        }

        if (text.Contains("not authorized", StringComparison.Ordinal))
        {
            return "twitter_login_required";
        }

        return null;
    }
}

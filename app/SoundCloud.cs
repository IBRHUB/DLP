internal static class SoundCloud
{
    public const string DisplayName = "SoundCloud";

    private static readonly string[] Hosts =
    [
        "soundcloud.com",
        "www.soundcloud.com",
        "m.soundcloud.com",
        "on.soundcloud.com"
    ];

    public static bool IsHost(string host) => YtDlpSites.HostMatches(host, Hosts);

    public static bool IsMediaPage(Uri uri, string host)
    {
        if (!IsHost(host))
        {
            return false;
        }

        string path = uri.AbsolutePath.ToLowerInvariant();
        string[] ignoredPaths = ["/", "/discover", "/stream", "/you", "/upload", "/search"];

        return !ignoredPaths.Any(ignoredPath =>
            string.Equals(path, ignoredPath, StringComparison.Ordinal)
            || path.StartsWith(ignoredPath + "/", StringComparison.Ordinal));
    }
}

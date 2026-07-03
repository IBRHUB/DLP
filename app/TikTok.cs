using System.Text.RegularExpressions;

internal static class TikTok
{
    public const string DisplayName = "TikTok";

    private static readonly string[] Hosts =
    [
        "tiktok.com",
        "www.tiktok.com",
        "m.tiktok.com",
        "vm.tiktok.com",
        "vt.tiktok.com"
    ];

    public static bool IsHost(string host) => YtDlpSites.HostMatches(host, Hosts);

    public static bool IsMediaPage(Uri uri, string host)
    {
        if (!IsHost(host))
        {
            return false;
        }

        if (host is "vm.tiktok.com" or "vt.tiktok.com")
        {
            return true;
        }

        return Regex.IsMatch(uri.AbsolutePath, @"/@[^/]+/video/\d+", RegexOptions.IgnoreCase);
    }
}

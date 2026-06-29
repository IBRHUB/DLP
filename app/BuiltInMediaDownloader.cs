using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

internal static class BuiltInMediaDownloader
{
    private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly HashSet<string> DirectMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".webm",
        ".m4v",
        ".mov",
        ".m4a",
        ".mp3",
        ".aac",
        ".opus"
    };

    public static bool CanDownload(string url, string? audioUrl)
    {
        return IsDirectMediaUrl(url);
    }

    public static async Task<bool> DownloadAsync(
        string url,
        string? audioUrl,
        string downloadDirectory,
        string? title,
        string? referer,
        string? userAgent,
        string? ffmpegPath,
        bool createDuplicateCopy,
        Action<string> log,
        Action<string, int>? statusChanged,
        Action<Process?>? processChanged)
    {
        Directory.CreateDirectory(downloadDirectory);

        string extension = GetDirectMediaExtension(url, ".mp4");
        string outputPath = BuildOutputPath(downloadDirectory, title, url, extension, createDuplicateCopy);

        statusChanged?.Invoke("Downloading with DLP", 0);
        await DownloadFileAsync(url, outputPath, referer, userAgent, 0, 100, log, statusChanged);
        log($"Built-in media download completed: {outputPath}");
        statusChanged?.Invoke("Done - saved in Downloads\\DLP", 100);
        return true;
    }

    private static async Task DownloadFileAsync(
        string url,
        string outputPath,
        string? referer,
        string? userAgent,
        int progressStart,
        int progressEnd,
        Action<string> log,
        Action<string, int>? statusChanged)
    {
        Exception? lastError = null;

        foreach (RequestProfile profile in GetRequestProfiles(url, referer))
        {
            try
            {
                await DownloadFileWithProfileAsync(
                    url,
                    outputPath,
                    userAgent,
                    profile,
                    progressStart,
                    progressEnd,
                    log,
                    statusChanged);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                log($"Built-in direct attempt failed ({profile.Name}): {ex.Message}");
            }
        }

        throw lastError ?? new InvalidOperationException("Direct request failed");
    }

    private static async Task DownloadFileWithProfileAsync(
        string url,
        string outputPath,
        string? userAgent,
        RequestProfile profile,
        int progressStart,
        int progressEnd,
        Action<string> log,
        Action<string, int>? statusChanged)
    {
        try
        {
            using HttpResponseMessage response = await SendDownloadRequestAsync(url, userAgent, profile, log);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Direct request failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            long? contentLength = response.Content.Headers.ContentLength;

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            await using Stream input = await response.Content.ReadAsStreamAsync();
            await using FileStream output = new(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 128,
                useAsync: true);

            byte[] buffer = new byte[1024 * 128];
            long totalRead = 0;

            while (true)
            {
                int bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length));

                if (bytesRead == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                if (contentLength is > 0)
                {
                    double ratio = Math.Clamp((double)totalRead / contentLength.Value, 0, 1);
                    int progress = progressStart + (int)Math.Round((progressEnd - progressStart) * ratio);
                    statusChanged?.Invoke($"Downloading with DLP {progress}%", progress);
                }
            }

            await output.FlushAsync();
            log($"Built-in direct file saved: {outputPath}");
        }
        catch
        {
            throw;
        }
    }

    private static async Task<HttpResponseMessage> SendDownloadRequestAsync(
        string url,
        string? userAgent,
        RequestProfile profile,
        Action<string> log)
    {
        string currentUrl = url;
        string? currentReferer = profile.Referrer;

        for (int redirectCount = 0; redirectCount < 8; redirectCount++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, currentUrl);
            AddRequestHeaders(request, currentReferer, userAgent, profile);

            HttpResponseMessage response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
            {
                return response;
            }

            string nextUrl = ResolveRedirectUrl(currentUrl, response.Headers.Location);
            response.Dispose();

            if (!Uri.TryCreate(nextUrl, UriKind.Absolute, out Uri? nextUri)
                || !string.Equals(nextUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Direct request redirect was not a valid HTTPS URL");
            }

            log($"Built-in direct redirect resolved: {DescribeUrl(currentUrl)} -> {DescribeUrl(nextUrl)}");

            if (profile.Navigation)
            {
                currentReferer = currentUrl;
            }

            currentUrl = nextUrl;
        }

        throw new InvalidOperationException("Direct request had too many redirects");
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        return code is >= 300 and <= 399;
    }

    private static string ResolveRedirectUrl(string currentUrl, Uri location)
    {
        if (location.IsAbsoluteUri)
        {
            return location.AbsoluteUri;
        }

        return Uri.TryCreate(currentUrl, UriKind.Absolute, out Uri? currentUri)
            && Uri.TryCreate(currentUri, location, out Uri? resolvedUri)
            ? resolvedUri.AbsoluteUri
            : location.ToString();
    }

    private static string DescribeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return "unknown";
        }

        string fileName = GetQueryMediaFileName(uri)
            ?? Path.GetFileName(WebUtility.UrlDecode(uri.AbsolutePath).TrimEnd('/'));

        return string.IsNullOrWhiteSpace(fileName)
            ? uri.Host
            : $"{uri.Host}/{fileName}";
    }

    private static void AddRequestHeaders(HttpRequestMessage request, string? referer, string? userAgent, RequestProfile profile)
    {
        request.Headers.TryAddWithoutValidation("Accept", profile.Accept);
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", profile.FetchDest);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", profile.FetchMode);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", profile.FetchSite);

        if (profile.Navigation)
        {
            request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
            request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        }

        if (!string.IsNullOrWhiteSpace(userAgent)
            && request.Headers.UserAgent.TryParseAdd(userAgent))
        {
            if (Uri.TryCreate(referer, UriKind.Absolute, out Uri? parsedReferer))
            {
                request.Headers.Referrer = parsedReferer;
            }

            return;
        }

        request.Headers.UserAgent.ParseAdd(DefaultUserAgent);

        if (Uri.TryCreate(referer, UriKind.Absolute, out Uri? refererUri))
        {
            request.Headers.Referrer = refererUri;
        }
    }

    private static IEnumerable<RequestProfile> GetRequestProfiles(string url, string? referer)
    {
        string? safeReferer = Uri.TryCreate(referer, UriKind.Absolute, out Uri? refererUri)
            ? refererUri.AbsoluteUri
            : null;
        string? refererOrigin = refererUri is null ? null : GetOrigin(refererUri);
        string? targetOrigin = Uri.TryCreate(url, UriKind.Absolute, out Uri? targetUri)
            ? GetOrigin(targetUri)
            : null;

        if (!string.IsNullOrWhiteSpace(safeReferer))
        {
            yield return RequestProfile.NavigationRequest("with-referrer", safeReferer, "same-origin");
        }

        yield return RequestProfile.NavigationRequest("no-referrer", null, "none");

        if (!string.IsNullOrWhiteSpace(refererOrigin)
            && !string.Equals(refererOrigin, safeReferer, StringComparison.OrdinalIgnoreCase))
        {
            yield return RequestProfile.NavigationRequest("referrer-origin", refererOrigin, "same-origin");
        }

        if (!string.IsNullOrWhiteSpace(targetOrigin)
            && !string.Equals(targetOrigin, refererOrigin, StringComparison.OrdinalIgnoreCase))
        {
            yield return RequestProfile.NavigationRequest("target-origin", targetOrigin, "same-origin");
        }

        yield return RequestProfile.MediaRequest("media-no-referrer", null);
    }

    private static string GetOrigin(Uri uri) => $"{uri.Scheme}://{uri.Host}/";

    private static bool IsDirectMediaUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && DirectMediaExtensions.Contains(GetMediaExtension(uri));
    }

    private static string GetDirectMediaExtension(string url, string fallback)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return fallback;
        }

        string extension = GetMediaExtension(uri);
        return DirectMediaExtensions.Contains(extension) ? extension.ToLowerInvariant() : fallback;
    }

    private static string GetMediaExtension(Uri uri)
    {
        string pathExtension = Path.GetExtension(WebUtility.UrlDecode(uri.AbsolutePath).TrimEnd('/'));

        if (DirectMediaExtensions.Contains(pathExtension))
        {
            return pathExtension;
        }

        string? mediaFileName = GetQueryMediaFileName(uri);
        return string.IsNullOrWhiteSpace(mediaFileName) ? pathExtension : Path.GetExtension(mediaFileName);
    }

    private static string? GetQueryMediaFileName(Uri uri)
    {
        foreach (string parameterName in new[] { "file", "filename", "name", "src" })
        {
            string? value = GetQueryValue(uri, parameterName);

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string fileName = Path.GetFileName(value.TrimEnd('/'));

            if (DirectMediaExtensions.Contains(Path.GetExtension(fileName)))
            {
                return fileName;
            }
        }

        return null;
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

    private static string BuildOutputPath(
        string downloadDirectory,
        string? title,
        string url,
        string extension,
        bool createDuplicateCopy)
    {
        string baseName = BuildSafeBaseName(title, url);

        if (createDuplicateCopy)
        {
            baseName = $"{baseName} copy-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        }

        string outputPath = Path.Combine(downloadDirectory, $"{baseName}{extension}");

        if (!File.Exists(outputPath))
        {
            return outputPath;
        }

        return Path.Combine(downloadDirectory, $"{baseName} copy-{DateTimeOffset.Now:yyyyMMdd-HHmmss}{extension}");
    }

    private static string BuildSafeBaseName(string? title, string url)
    {
        string value = !string.IsNullOrWhiteSpace(title)
            ? title.Trim()
            : GetUrlFileStem(url);

        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidCharacter, ' ');
        }

        value = WhitespaceRegex.Replace(value, " ").Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            value = $"DLP {DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        }

        return value.Length <= 160 ? value : value[..160].Trim();
    }

    private static string GetUrlFileStem(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return "DLP media";
        }

        string? queryFileName = GetQueryMediaFileName(uri);

        if (!string.IsNullOrWhiteSpace(queryFileName))
        {
            return Path.GetFileNameWithoutExtension(queryFileName);
        }

        return Path.GetFileNameWithoutExtension(WebUtility.UrlDecode(uri.AbsolutePath).TrimEnd('/'));
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private sealed record RequestProfile(
        string Name,
        string? Referrer,
        string Accept,
        string FetchDest,
        string FetchMode,
        string FetchSite,
        bool Navigation)
    {
        public static RequestProfile NavigationRequest(string name, string? referrer, string fetchSite) => new(
            name,
            referrer,
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            "document",
            "navigate",
            fetchSite,
            true);

        public static RequestProfile MediaRequest(string name, string? referrer) => new(
            name,
            referrer,
            "*/*",
            "video",
            "no-cors",
            referrer is null ? "none" : "same-origin",
            false);
    }
}

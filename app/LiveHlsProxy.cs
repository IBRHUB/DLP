using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal sealed class LiveHlsProxy : IDisposable
{
    private const int RequestHeaderLimit = 65536;
    private const int RemoteFetchAttempts = 4;
    private const int MaxPrefetchSegments = 4;
    private const long MaxCachedBytes = 96L * 1024L * 1024L;
    private const int MaxCachedItemBytes = 32 * 1024 * 1024;
    private const long StableVariantMaxBandwidth = 4_500_000;
    private const int StableVariantMaxHeight = 720;
    private const int StableLiveStartTargetDurationsBehind = 3;
    private static readonly TimeSpan SegmentCacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ResourceCacheDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LastGoodPlaylistDuration = TimeSpan.FromMinutes(2);
    private static readonly Regex UriAttributeRegex = new(@"URI=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly TcpListener _listener;
    private readonly HttpClient _httpClient;
    private readonly string _sourceUrl;
    private readonly string? _referer;
    private readonly string? _userAgent;
    private readonly string _token;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _inflightBytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedBytes> _byteCache = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();
    private readonly object _playlistLock = new();
    private long _cachedBytes;
    private string? _lastGoodPlaylist;
    private DateTimeOffset _lastGoodPlaylistUtc;

    private LiveHlsProxy(string sourceUrl, string? referer, string? userAgent)
    {
        _sourceUrl = sourceUrl;
        _referer = referer;
        _userAgent = userAgent;
        _token = CreateToken();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public string PlaylistUrl
    {
        get
        {
            IPEndPoint endpoint = (IPEndPoint)_listener.LocalEndpoint;
            return $"http://127.0.0.1:{endpoint.Port}/{_token}/index.m3u8";
        }
    }

    public static async Task<int> RunVlcAsync(string streamUrl, string? title, string? referer, string? userAgent)
    {
        using LiveHlsProxy proxy = new(streamUrl, referer, userAgent);
        proxy.Start();

        using Process? vlc = StartVlc(proxy.PlaylistUrl, title);

        if (vlc is null)
        {
            Program.Log("Live stream failed: VLC was not found");
            ApplicationConfiguration.Initialize();
            MessageBox.Show(
                "VLC was not found. Install VLC or add vlc.exe to PATH.",
                "DLP",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 1;
        }

        Program.Log($"Live HLS proxy started: source={streamUrl} local={proxy.PlaylistUrl}");

        using CancellationTokenSource timeout = new(TimeSpan.FromHours(8));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(proxy._stop.Token, timeout.Token);

        try
        {
            await vlc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(vlc);
        }

        return 0;
    }

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoopAsync(_stop.Token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _httpClient.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Program.Log($"Live proxy accept failed: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                string? request = await ReadHttpRequestAsync(stream, cancellationToken);

                if (string.IsNullOrWhiteSpace(request))
                {
                    return;
                }

                string[] lines = request.Split(["\r\n"], StringSplitOptions.None);
                string[] firstLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

                if (firstLine.Length < 2 || !string.Equals(firstLine[0], "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextAsync(stream, 405, "text/plain; charset=utf-8", "Only GET is supported", cancellationToken);
                    return;
                }

                await RouteAsync(stream, firstLine[1], cancellationToken);
            }
            catch (Exception ex)
            {
                Program.Log($"Live proxy request failed: {ex.Message}");
            }
        }
    }

    private async Task RouteAsync(NetworkStream stream, string rawTarget, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate($"http://127.0.0.1{rawTarget}", UriKind.Absolute, out Uri? localUri))
        {
            await WriteTextAsync(stream, 400, "text/plain; charset=utf-8", "Invalid request", cancellationToken);
            return;
        }

        string[] parts = localUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2 || !string.Equals(parts[0], _token, StringComparison.Ordinal))
        {
            await WriteTextAsync(stream, 404, "text/plain; charset=utf-8", "Not found", cancellationToken);
            return;
        }

        if (parts.Length == 2 && string.Equals(parts[1], "index.m3u8", StringComparison.OrdinalIgnoreCase))
        {
            string playlist = await BuildPlaylistAsync(cancellationToken);
            await WriteBytesAsync(
                stream,
                200,
                "application/vnd.apple.mpegurl; charset=utf-8",
                Encoding.UTF8.GetBytes(playlist),
                cancellationToken);
            return;
        }

        if (parts.Length == 3 && string.Equals(parts[1], "segment", StringComparison.OrdinalIgnoreCase))
        {
            string remoteUrl = DecodeUrl(parts[2]);
            byte[] mediaBytes = await GetCachedBytesAsync(
                $"segment:{remoteUrl}",
                remoteUrl,
                stripTransportStreamPrefix: true,
                SegmentCacheDuration,
                cancellationToken);
            await WriteBytesAsync(stream, 200, "video/mp2t", mediaBytes, cancellationToken);
            return;
        }

        if (parts.Length == 3 && string.Equals(parts[1], "resource", StringComparison.OrdinalIgnoreCase))
        {
            string remoteUrl = DecodeUrl(parts[2]);
            byte[] resourceBytes = await GetCachedBytesAsync(
                $"resource:{remoteUrl}",
                remoteUrl,
                stripTransportStreamPrefix: false,
                ResourceCacheDuration,
                cancellationToken);
            await WriteBytesAsync(stream, 200, "application/octet-stream", resourceBytes, cancellationToken);
            return;
        }

        await WriteTextAsync(stream, 404, "text/plain; charset=utf-8", "Not found", cancellationToken);
    }

    private async Task<string> BuildPlaylistAsync(CancellationToken cancellationToken)
    {
        try
        {
            PlaylistBuildResult result = await BuildPlaylistCoreAsync(cancellationToken);

            lock (_playlistLock)
            {
                _lastGoodPlaylist = result.Playlist;
                _lastGoodPlaylistUtc = DateTimeOffset.UtcNow;
            }

            QueueSegmentPrefetch(result.SegmentUrls, result.IsLive);
            return result.Playlist;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && TryGetLastGoodPlaylist(out string playlist))
        {
            Program.Log($"Live proxy playlist fallback used after fetch failure: {ex.Message}");
            return playlist;
        }
    }

    private async Task<PlaylistBuildResult> BuildPlaylistCoreAsync(CancellationToken cancellationToken)
    {
        string playlistUrl = _sourceUrl;
        string playlist = await FetchTextAsync(playlistUrl, cancellationToken);
        IReadOnlyList<string> variantUrls = SelectStableVariantUrls(playlist, playlistUrl);

        if (variantUrls.Count > 0)
        {
            for (int i = 0; i < variantUrls.Count; i++)
            {
                try
                {
                    playlistUrl = variantUrls[i];
                    playlist = await FetchTextAsync(playlistUrl, cancellationToken);
                    break;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && i < variantUrls.Count - 1)
                {
                    Program.Log($"Live proxy variant failed, trying fallback: {GetErrorSummary(ex)} url={ShortenUrl(variantUrls[i])}");
                }
            }
        }

        string[] lines = playlist.Split('\n');
        bool isLive = !playlist.Contains("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase);
        bool shouldAddLiveStart = isLive && !playlist.Contains("#EXT-X-START:", StringComparison.OrdinalIgnoreCase);
        int liveStartOffsetSeconds = GetLiveStartOffsetSeconds(lines);
        bool liveStartAdded = false;
        StringBuilder output = new(playlist.Length + 1024);
        List<string> segmentUrls = [];

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                output.AppendLine();
                continue;
            }

            if (shouldAddLiveStart
                && !liveStartAdded
                && trimmed.Equals("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                output.AppendLine(line);
                output.AppendLine($"#EXT-X-START:TIME-OFFSET=-{liveStartOffsetSeconds},PRECISE=NO");
                liveStartAdded = true;
                continue;
            }

            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                output.AppendLine(RewriteUriAttributes(line, playlistUrl));
                continue;
            }

            string absoluteUrl = ToAbsoluteUrl(trimmed, playlistUrl);
            segmentUrls.Add(absoluteUrl);
            output.AppendLine(GetLocalSegmentUrl(absoluteUrl));
        }

        return new PlaylistBuildResult(output.ToString(), segmentUrls, isLive);
    }

    private string RewriteUriAttributes(string line, string baseUrl)
    {
        return UriAttributeRegex.Replace(line, match =>
        {
            string absoluteUrl = ToAbsoluteUrl(match.Groups[1].Value, baseUrl);
            return $"URI=\"{GetLocalResourceUrl(absoluteUrl)}\"";
        });
    }

    private async Task<string> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        return await ExecuteRemoteFetchWithRetryAsync(
            url,
            "playlist",
            retryNotFound: false,
            async token =>
            {
                using HttpResponseMessage response = await SendRemoteRequestAsync(url, HttpMethod.Get, token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(token);
            },
            cancellationToken);
    }

    private async Task<byte[]> FetchBytesAsync(string url, CancellationToken cancellationToken)
    {
        return await ExecuteRemoteFetchWithRetryAsync(
            url,
            "bytes",
            retryNotFound: true,
            async token =>
            {
                using HttpResponseMessage response = await SendRemoteRequestAsync(url, HttpMethod.Get, token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(token);
            },
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRemoteRequestAsync(string url, HttpMethod method, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(method, url);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");

        if (!string.IsNullOrWhiteSpace(_referer))
        {
            request.Headers.Referrer = new Uri(_referer);

            if (Uri.TryCreate(_referer, UriKind.Absolute, out Uri? refererUri))
            {
                request.Headers.TryAddWithoutValidation("Origin", refererUri.GetLeftPart(UriPartial.Authority));
            }
        }

        if (!string.IsNullOrWhiteSpace(_userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        }

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<T> ExecuteRemoteFetchWithRetryAsync<T>(
        string url,
        string operation,
        bool retryNotFound,
        Func<CancellationToken, Task<T>> fetch,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (int attempt = 1; attempt <= RemoteFetchAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await fetch(cancellationToken);
            }
            catch (Exception ex) when (attempt < RemoteFetchAttempts && IsRetryableRemoteFetchError(ex, retryNotFound, cancellationToken))
            {
                lastError = ex;
                Program.Log($"Live proxy {operation} retry {attempt}/{RemoteFetchAttempts}: {GetErrorSummary(ex)} url={ShortenUrl(url)}");
                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
            }
            catch
            {
                throw;
            }
        }

        throw lastError ?? new InvalidOperationException("Remote fetch failed");
    }

    private async Task<byte[]> GetCachedBytesAsync(
        string cacheKey,
        string remoteUrl,
        bool stripTransportStreamPrefix,
        TimeSpan cacheDuration,
        CancellationToken cancellationToken)
    {
        if (TryGetCachedBytes(cacheKey, out byte[] cachedBytes))
        {
            return cachedBytes;
        }

        Lazy<Task<byte[]>> lazy = _inflightBytes.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<byte[]>>(
                () => FetchAndCacheBytesAsync(cacheKey, remoteUrl, stripTransportStreamPrefix, cacheDuration, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value;
        }
        finally
        {
            RemoveInflightBytes(cacheKey, lazy);
        }
    }

    private async Task<byte[]> FetchAndCacheBytesAsync(
        string cacheKey,
        string remoteUrl,
        bool stripTransportStreamPrefix,
        TimeSpan cacheDuration,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await FetchBytesAsync(remoteUrl, cancellationToken);

        if (stripTransportStreamPrefix)
        {
            bytes = StripTransportStreamPrefix(bytes);
        }

        StoreCachedBytes(cacheKey, bytes, cacheDuration);
        return bytes;
    }

    private void QueueSegmentPrefetch(IReadOnlyList<string> segmentUrls, bool isLive)
    {
        int start = isLive ? Math.Max(0, segmentUrls.Count - MaxPrefetchSegments) : 0;
        int end = isLive ? segmentUrls.Count : Math.Min(segmentUrls.Count, MaxPrefetchSegments);

        for (int i = start; i < end; i++)
        {
            string remoteUrl = segmentUrls[i];
            string cacheKey = $"segment:{remoteUrl}";

            if (TryGetCachedBytes(cacheKey, out _) || _inflightBytes.ContainsKey(cacheKey))
            {
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await GetCachedBytesAsync(
                        cacheKey,
                        remoteUrl,
                        stripTransportStreamPrefix: true,
                        SegmentCacheDuration,
                        _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    // The proxy is shutting down.
                }
                catch (Exception ex)
                {
                    Program.Log($"Live proxy segment prefetch failed: {GetErrorSummary(ex)} url={ShortenUrl(remoteUrl)}");
                }
            }, CancellationToken.None);
        }
    }

    private bool TryGetLastGoodPlaylist(out string playlist)
    {
        lock (_playlistLock)
        {
            if (!string.IsNullOrWhiteSpace(_lastGoodPlaylist)
                && DateTimeOffset.UtcNow - _lastGoodPlaylistUtc <= LastGoodPlaylistDuration)
            {
                playlist = _lastGoodPlaylist;
                return true;
            }

            playlist = string.Empty;
            return false;
        }
    }

    private bool TryGetCachedBytes(string cacheKey, out byte[] bytes)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_cacheLock)
        {
            if (_byteCache.TryGetValue(cacheKey, out CachedBytes? cached))
            {
                if (cached.ExpiresAtUtc > now)
                {
                    cached.LastAccessUtc = now;
                    bytes = cached.Bytes;
                    return true;
                }

                RemoveCachedBytesLocked(cacheKey, cached);
            }
        }

        bytes = [];
        return false;
    }

    private void StoreCachedBytes(string cacheKey, byte[] bytes, TimeSpan cacheDuration)
    {
        if (bytes.Length == 0 || bytes.Length > MaxCachedItemBytes)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_cacheLock)
        {
            if (_byteCache.TryGetValue(cacheKey, out CachedBytes? previous))
            {
                RemoveCachedBytesLocked(cacheKey, previous);
            }

            CachedBytes cached = new(bytes, now.Add(cacheDuration), now);
            _byteCache[cacheKey] = cached;
            _cachedBytes += bytes.LongLength;
            TrimCacheLocked(now);
        }
    }

    private void TrimCacheLocked(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, CachedBytes> item in _byteCache.ToArray())
        {
            if (item.Value.ExpiresAtUtc <= now)
            {
                RemoveCachedBytesLocked(item.Key, item.Value);
            }
        }

        if (_cachedBytes <= MaxCachedBytes)
        {
            return;
        }

        foreach (KeyValuePair<string, CachedBytes> item in _byteCache
            .OrderBy(entry => entry.Value.LastAccessUtc)
            .ToArray())
        {
            RemoveCachedBytesLocked(item.Key, item.Value);

            if (_cachedBytes <= MaxCachedBytes)
            {
                return;
            }
        }
    }

    private void RemoveCachedBytesLocked(string cacheKey, CachedBytes cached)
    {
        if (_byteCache.Remove(cacheKey))
        {
            _cachedBytes = Math.Max(0, _cachedBytes - cached.Bytes.LongLength);
        }
    }

    private void RemoveInflightBytes(string cacheKey, Lazy<Task<byte[]>> lazy)
    {
        if (_inflightBytes.TryGetValue(cacheKey, out Lazy<Task<byte[]>>? current)
            && ReferenceEquals(current, lazy))
        {
            _inflightBytes.TryRemove(cacheKey, out _);
        }
    }

    private string GetLocalSegmentUrl(string remoteUrl) => $"{GetBaseUrl()}/segment/{EncodeUrl(remoteUrl)}";

    private string GetLocalResourceUrl(string remoteUrl) => $"{GetBaseUrl()}/resource/{EncodeUrl(remoteUrl)}";

    private string GetBaseUrl()
    {
        IPEndPoint endpoint = (IPEndPoint)_listener.LocalEndpoint;
        return $"http://127.0.0.1:{endpoint.Port}/{_token}";
    }

    private static IReadOnlyList<string> SelectStableVariantUrls(string playlist, string baseUrl)
    {
        string[] lines = playlist.Split('\n');
        string? pendingStreamInfo = null;
        List<HlsVariant> variants = [];

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                pendingStreamInfo = line;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(pendingStreamInfo)
                && line.Length > 0
                && !line.StartsWith("#", StringComparison.Ordinal))
            {
                HlsVariant? variant = CreateHlsVariant(pendingStreamInfo, line, baseUrl);

                if (variant is not null)
                {
                    variants.Add(variant);
                }

                pendingStreamInfo = null;
            }
        }

        return variants.Count == 0
            ? []
            : OrderStableVariants(variants)
                .Select(variant => variant.Url)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
    }

    private static HlsVariant? CreateHlsVariant(string streamInfo, string urlLine, string baseUrl)
    {
        try
        {
            return new HlsVariant(
                ToAbsoluteUrl(urlLine, baseUrl),
                ReadLongAttribute(streamInfo, "AVERAGE-BANDWIDTH")
                    ?? ReadLongAttribute(streamInfo, "BANDWIDTH")
                    ?? 1,
                ReadResolutionHeight(streamInfo));
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<HlsVariant> OrderStableVariants(IReadOnlyList<HlsVariant> variants)
    {
        HlsVariant[] videoVariants = variants
            .Where(variant => variant.Height > 0)
            .ToArray();

        if (videoVariants.Length > 0)
        {
            HlsVariant[] stableVideo = videoVariants
                .Where(variant => variant.Height <= StableVariantMaxHeight && variant.Bandwidth <= StableVariantMaxBandwidth)
                .OrderByDescending(variant => variant.Height)
                .ThenByDescending(variant => variant.Bandwidth)
                .ToArray();

            HlsVariant[] stableHeight = videoVariants
                .Where(variant => variant.Height <= StableVariantMaxHeight && variant.Bandwidth > StableVariantMaxBandwidth)
                .OrderByDescending(variant => variant.Height)
                .ThenBy(variant => variant.Bandwidth)
                .ToArray();

            HlsVariant[] largerVideo = videoVariants
                .Where(variant => variant.Height > StableVariantMaxHeight)
                .OrderBy(variant => variant.Height)
                .ThenBy(variant => variant.Bandwidth)
                .ToArray();

            HlsVariant[] unknownResolution = variants
                .Where(variant => variant.Height <= 0)
                .OrderByDescending(variant => variant.Bandwidth)
                .ToArray();

            return stableVideo
                .Concat(stableHeight)
                .Concat(largerVideo)
                .Concat(unknownResolution)
                .ToArray();
        }

        HlsVariant[] stableBandwidth = variants
            .Where(variant => variant.Bandwidth <= StableVariantMaxBandwidth)
            .OrderByDescending(variant => variant.Bandwidth)
            .ToArray();

        HlsVariant[] largerBandwidth = variants
            .Where(variant => variant.Bandwidth > StableVariantMaxBandwidth)
            .OrderBy(variant => variant.Bandwidth)
            .ToArray();

        return stableBandwidth
            .Concat(largerBandwidth)
            .ToArray();
    }

    private static long? ReadLongAttribute(string line, string attributeName)
    {
        Match match = Regex.Match(line, $@"(?:^|,){Regex.Escape(attributeName)}=(\d+)", RegexOptions.IgnoreCase);

        return match.Success && long.TryParse(match.Groups[1].Value, out long value)
            ? value
            : null;
    }

    private static int ReadResolutionHeight(string line)
    {
        Match match = Regex.Match(line, @"(?:^|,)RESOLUTION=\d+x(\d+)", RegexOptions.IgnoreCase);

        return match.Success && int.TryParse(match.Groups[1].Value, out int height)
            ? height
            : 0;
    }

    private static int GetLiveStartOffsetSeconds(IEnumerable<string> playlistLines)
    {
        foreach (string rawLine in playlistLines)
        {
            string line = rawLine.Trim();

            if (!line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = line["#EXT-X-TARGETDURATION:".Length..].Trim();

            if (int.TryParse(value, out int targetDuration) && targetDuration > 0)
            {
                return targetDuration * StableLiveStartTargetDurationsBehind;
            }
        }

        return 6;
    }

    private static bool IsRetryableRemoteFetchError(Exception ex, bool retryNotFound, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode is HttpStatusCode statusCode)
        {
            return IsRetryableStatusCode(statusCode, retryNotFound);
        }

        return ex is HttpRequestException
            || ex is TaskCanceledException
            || ex is TimeoutException
            || ex is IOException;
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode, bool retryNotFound)
    {
        int code = (int)statusCode;

        return code >= 500
            || statusCode == HttpStatusCode.RequestTimeout
            || statusCode == HttpStatusCode.TooManyRequests
            || (retryNotFound && statusCode == HttpStatusCode.NotFound);
    }

    private static TimeSpan GetRetryDelay(int attempt)
    {
        return attempt switch
        {
            1 => TimeSpan.FromMilliseconds(250),
            2 => TimeSpan.FromMilliseconds(650),
            3 => TimeSpan.FromMilliseconds(1200),
            _ => TimeSpan.FromMilliseconds(1800)
        };
    }

    private static string GetErrorSummary(Exception ex)
    {
        if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode is HttpStatusCode statusCode)
        {
            return $"HTTP {(int)statusCode}";
        }

        return ex.GetType().Name;
    }

    private static string ShortenUrl(string url)
    {
        return url.Length <= 180 ? url : url[..177] + "...";
    }

    private static byte[] StripTransportStreamPrefix(byte[] bytes)
    {
        int offset = FindMpegTsOffset(bytes);

        if (offset <= 0)
        {
            return bytes;
        }

        byte[] stripped = new byte[bytes.Length - offset];
        Buffer.BlockCopy(bytes, offset, stripped, 0, stripped.Length);
        return stripped;
    }

    private static int FindMpegTsOffset(byte[] bytes)
    {
        int maxOffset = Math.Min(256, bytes.Length - 376);

        for (int offset = 0; offset <= maxOffset; offset++)
        {
            if (bytes[offset] == 0x47
                && bytes[offset + 188] == 0x47
                && bytes[offset + 376] == 0x47)
            {
                return offset;
            }
        }

        return 0;
    }

    private static async Task<string?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[2048];
        using MemoryStream request = new();

        while (request.Length < RequestHeaderLimit)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

            if (read <= 0)
            {
                return null;
            }

            request.Write(buffer, 0, read);

            byte[] bytes = request.ToArray();

            if (ContainsHeaderTerminator(bytes))
            {
                return Encoding.ASCII.GetString(bytes);
            }
        }

        return null;
    }

    private static bool ContainsHeaderTerminator(byte[] bytes)
    {
        for (int i = 3; i < bytes.Length; i++)
        {
            if (bytes[i - 3] == '\r'
                && bytes[i - 2] == '\n'
                && bytes[i - 1] == '\r'
                && bytes[i] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WriteTextAsync(NetworkStream stream, int statusCode, string contentType, string text, CancellationToken cancellationToken)
    {
        await WriteBytesAsync(stream, statusCode, contentType, Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    private static async Task WriteBytesAsync(NetworkStream stream, int statusCode, string contentType, byte[] body, CancellationToken cancellationToken)
    {
        string reason = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error"
        };
        string header =
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static string ToAbsoluteUrl(string value, string baseUrl)
    {
        return new Uri(new Uri(baseUrl), value).AbsoluteUri;
    }

    private static string EncodeUrl(string url)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(url))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string DecodeUrl(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');

        while (padded.Length % 4 != 0)
        {
            padded += "=";
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Process? StartVlc(string playlistUrl, string? title)
    {
        string? vlcPath = FindVlcPath();

        if (vlcPath is null)
        {
            return null;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = vlcPath,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        startInfo.ArgumentList.Add("--network-caching=6000");
        startInfo.ArgumentList.Add("--http-reconnect");
        startInfo.ArgumentList.Add("--meta-title");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(title) ? "DLP Live Stream" : title.Trim());
        startInfo.ArgumentList.Add(playlistUrl);

        return Process.Start(startInfo);
    }

    private static string? FindVlcPath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC", "vlc.exe")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");

        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory.Trim(), "vlc.exe");

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // VLC may already be closed.
        }
    }

    private sealed record PlaylistBuildResult(string Playlist, IReadOnlyList<string> SegmentUrls, bool IsLive);

    private sealed record HlsVariant(string Url, long Bandwidth, int Height);

    private sealed class CachedBytes(byte[] bytes, DateTimeOffset expiresAtUtc, DateTimeOffset lastAccessUtc)
    {
        public byte[] Bytes { get; } = bytes;

        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

        public DateTimeOffset LastAccessUtc { get; set; } = lastAccessUtc;
    }
}

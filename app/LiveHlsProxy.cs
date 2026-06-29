using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal sealed class LiveHlsProxy : IDisposable
{
    private const int RequestHeaderLimit = 65536;
    private static readonly Regex UriAttributeRegex = new(@"URI=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly TcpListener _listener;
    private readonly HttpClient _httpClient;
    private readonly string _sourceUrl;
    private readonly string? _referer;
    private readonly string? _userAgent;
    private readonly string _token;
    private readonly CancellationTokenSource _stop = new();

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
            byte[] mediaBytes = await FetchBytesAsync(remoteUrl, cancellationToken);
            mediaBytes = StripTransportStreamPrefix(mediaBytes);
            await WriteBytesAsync(stream, 200, "video/mp2t", mediaBytes, cancellationToken);
            return;
        }

        if (parts.Length == 3 && string.Equals(parts[1], "resource", StringComparison.OrdinalIgnoreCase))
        {
            string remoteUrl = DecodeUrl(parts[2]);
            byte[] resourceBytes = await FetchBytesAsync(remoteUrl, cancellationToken);
            await WriteBytesAsync(stream, 200, "application/octet-stream", resourceBytes, cancellationToken);
            return;
        }

        await WriteTextAsync(stream, 404, "text/plain; charset=utf-8", "Not found", cancellationToken);
    }

    private async Task<string> BuildPlaylistAsync(CancellationToken cancellationToken)
    {
        string playlistUrl = _sourceUrl;
        string playlist = await FetchTextAsync(playlistUrl, cancellationToken);
        string? variantUrl = SelectBestVariantUrl(playlist, playlistUrl);

        if (!string.IsNullOrWhiteSpace(variantUrl))
        {
            playlistUrl = variantUrl;
            playlist = await FetchTextAsync(playlistUrl, cancellationToken);
        }

        string[] lines = playlist.Split('\n');
        StringBuilder output = new(playlist.Length + 1024);

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                output.AppendLine();
                continue;
            }

            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                output.AppendLine(RewriteUriAttributes(line, playlistUrl));
                continue;
            }

            string absoluteUrl = ToAbsoluteUrl(trimmed, playlistUrl);
            output.AppendLine(GetLocalSegmentUrl(absoluteUrl));
        }

        return output.ToString();
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
        using HttpResponseMessage response = await SendRemoteRequestAsync(url, HttpMethod.Get, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<byte[]> FetchBytesAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendRemoteRequestAsync(url, HttpMethod.Get, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
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

    private string GetLocalSegmentUrl(string remoteUrl) => $"{GetBaseUrl()}/segment/{EncodeUrl(remoteUrl)}";

    private string GetLocalResourceUrl(string remoteUrl) => $"{GetBaseUrl()}/resource/{EncodeUrl(remoteUrl)}";

    private string GetBaseUrl()
    {
        IPEndPoint endpoint = (IPEndPoint)_listener.LocalEndpoint;
        return $"http://127.0.0.1:{endpoint.Port}/{_token}";
    }

    private static string? SelectBestVariantUrl(string playlist, string baseUrl)
    {
        string[] lines = playlist.Split('\n');
        long pendingBandwidth = 0;
        string? bestUrl = null;
        long bestBandwidth = -1;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                Match match = Regex.Match(line, @"BANDWIDTH=(\d+)", RegexOptions.IgnoreCase);
                pendingBandwidth = match.Success ? long.Parse(match.Groups[1].Value) : 1;
                continue;
            }

            if (pendingBandwidth > 0 && line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            {
                if (pendingBandwidth > bestBandwidth)
                {
                    bestUrl = ToAbsoluteUrl(line, baseUrl);
                    bestBandwidth = pendingBandwidth;
                }

                pendingBandwidth = 0;
            }
        }

        return bestUrl;
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

        startInfo.ArgumentList.Add("--network-caching=1200");
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
}

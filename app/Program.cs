using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Forms = System.Windows.Forms;

internal static class Program
{
    private const string InstanceMutexName = @"Local\DLP_MainWindow";
    private const string InstancePipeName = "DLP_MainWindow";
    private static readonly string[] DownloadMediaExtensions =
    [
        ".mp4",
        ".mkv",
        ".webm",
        ".mov",
        ".mp3",
        ".m4a",
        ".opus",
        ".wav",
        ".flac",
        ".aac"
    ];
    private static Mutex? InstanceMutex;
    private static DownloadWindow? ActiveWindow;

    [STAThread]
    private static int Main(string[] args)
    {
        string? source = ReadOption(args, "--source");
        string? url = ReadOption(args, "--url");
        string? audioUrl = NormalizeOptionalHttpsUrl(ReadOption(args, "--audio-url"));
        string? fallbackUrl = NormalizeOptionalHttpsUrl(ReadOption(args, "--fallback-url"));
        string? title = ReadOption(args, "--title");
        string? referer = NormalizeOptionalHttpsUrl(ReadOption(args, "--referer"));
        string? userAgent = NormalizeHeaderValue(ReadOption(args, "--user-agent"), 512);
        string? cookieBrowser = NormalizeCookieBrowser(ReadOption(args, "--browser-cookies"));
        string? openDownload = ReadOption(args, "--open-download");
        string? requestedStreamUrl = ReadOption(args, "--stream-url");
        bool silent = HasSwitch(args, "--silent");
        bool openApp = HasSwitch(args, "--open-app");
        bool openDownloads = HasSwitch(args, "--open-downloads");

        if (!string.IsNullOrWhiteSpace(requestedStreamUrl))
        {
            string? streamUrl = NormalizeOptionalHttpsUrl(requestedStreamUrl);

            if (streamUrl is null)
            {
                Log($"Rejected live stream URL: {requestedStreamUrl}");
                return 1;
            }

            return LiveHlsProxy.RunVlcAsync(streamUrl, title, referer, userAgent).GetAwaiter().GetResult();
        }

        if (string.IsNullOrWhiteSpace(url)
            && string.IsNullOrWhiteSpace(openDownload)
            && !openApp
            && !openDownloads
            && NativeMessagingHost.IsNativeMessagingInvocation())
        {
            return NativeMessagingHost.RunAsync().GetAwaiter().GetResult();
        }

        if (!silent)
        {
            if (TryForwardToExistingInstance(args))
            {
                return 0;
            }

            if (!TryBecomePrimaryInstance())
            {
                Log("DLP window is already running but did not accept the request");
                return 1;
            }

            StartInstanceCommandServer();
        }

        if (!string.IsNullOrWhiteSpace(openDownload))
        {
            OpenDownloadedFile(openDownload);
            return 0;
        }

        if (openApp)
        {
            ShowDownloadWindow(
                url: string.Empty,
                audioUrl: null,
                fallbackUrl: null,
                source: "manual",
                title: null,
                referer: null,
                userAgent: null,
                cookieBrowser: null);
            return 0;
        }

        if (openDownloads)
        {
            OpenDownloadFolder();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            ShowDownloadWindow(
                url: string.Empty,
                audioUrl: null,
                fallbackUrl: null,
                source: "manual",
                title: null,
                referer: null,
                userAgent: null,
                cookieBrowser: null);
            return 0;
        }

        Log($"Received URL from source '{source ?? "unknown"}': {url}");

        if (silent)
        {
            return SilentDownloader.DownloadVideoAsync(
                url,
                audioUrl,
                fallbackUrl,
                source ?? "unknown",
                title,
                referer,
                userAgent,
                cookieBrowser).GetAwaiter().GetResult();
        }

        ShowDownloadWindow(
            url,
            audioUrl,
            fallbackUrl,
            source ?? "unknown",
            title,
            referer,
            userAgent,
            cookieBrowser);

        return 0;
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasSwitch(string[] args, string name)
    {
        return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryForwardToExistingInstance(string[] args)
    {
        if (!HasExistingInstance())
        {
            return false;
        }

        string payload = JsonSerializer.Serialize(args);

        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using NamedPipeClientStream client = new(
                    ".",
                    InstancePipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                client.Connect(150);

                using StreamWriter writer = new(client, Encoding.UTF8, 4096, leaveOpen: false)
                {
                    AutoFlush = true
                };
                writer.WriteLine(payload);
                return true;
            }
            catch (TimeoutException)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }

        return false;
    }

    private static bool HasExistingInstance()
    {
        try
        {
            using Mutex existing = Mutex.OpenExisting(InstanceMutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryBecomePrimaryInstance()
    {
        InstanceMutex = new Mutex(initiallyOwned: false, InstanceMutexName);

        try
        {
            if (InstanceMutex.WaitOne(0))
            {
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            return true;
        }

        InstanceMutex.Dispose();
        InstanceMutex = null;
        return false;
    }

    private static void StartInstanceCommandServer()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using NamedPipeServerStream server = new(
                        InstancePipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync();

                    using StreamReader reader = new(server, Encoding.UTF8, false, 4096, leaveOpen: false);
                    string? line = await reader.ReadLineAsync();

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string[]? forwardedArgs = JsonSerializer.Deserialize<string[]>(line);

                    if (forwardedArgs is null || System.Windows.Application.Current is null)
                    {
                        continue;
                    }

                    System.Windows.Application.Current.Dispatcher.BeginInvoke(
                        new Action(() => HandleForwardedArguments(forwardedArgs)));
                }
                catch (Exception ex)
                {
                    Log($"Instance command server failed: {ex.Message}");
                    await Task.Delay(100);
                }
            }
        });
    }

    private static void HandleForwardedArguments(string[] args)
    {
        DownloadWindow? window = ActiveWindow;

        if (window is null)
        {
            return;
        }

        string? openDownload = ReadOption(args, "--open-download");
        string? url = NormalizeOptionalHttpsUrl(ReadOption(args, "--url"));

        if (!string.IsNullOrWhiteSpace(openDownload))
        {
            OpenDownloadedFile(openDownload);
            return;
        }

        if (HasSwitch(args, "--open-downloads"))
        {
            window.OpenDownloadsFromExternalRequest();
            return;
        }

        if (HasSwitch(args, "--open-app") || string.IsNullOrWhiteSpace(url))
        {
            window.ActivateFromExternalRequest();
            return;
        }

        window.ApplyExternalRequest(
            url,
            NormalizeOptionalHttpsUrl(ReadOption(args, "--audio-url")),
            NormalizeOptionalHttpsUrl(ReadOption(args, "--fallback-url")),
            ReadOption(args, "--source") ?? "browser",
            ReadOption(args, "--title"),
            NormalizeOptionalHttpsUrl(ReadOption(args, "--referer")),
            NormalizeHeaderValue(ReadOption(args, "--user-agent"), 512),
            NormalizeCookieBrowser(ReadOption(args, "--browser-cookies")));
    }

    public static string GetDownloadDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "DLP");

    public static string? NormalizeOptionalHttpsUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    public static string? NormalizeHeaderValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    public static string? NormalizeCookieBrowser(string? browser)
    {
        return CookieBrowserCatalog.Normalize(browser);
    }

    public static void OpenDownloadFolder()
    {
        CryptStatus access = Crypt.UnlockForCurrentUser();
        string downloadDirectory = access.Directory;

        Process.Start(new ProcessStartInfo
        {
            FileName = downloadDirectory,
            UseShellExecute = true
        });

        Log($"Opened download folder: {downloadDirectory}");
    }

    public static void OpenDownloadedFile(string fileName)
    {
        try
        {
            Crypt.UnlockForCurrentUser();
            string downloadDirectory = Path.GetFullPath(GetDownloadDirectory());
            string filePath = Path.GetFullPath(Path.Combine(downloadDirectory, fileName));
            string directoryPrefix = downloadDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (string.IsNullOrWhiteSpace(fileName)
                || fileName != Path.GetFileName(fileName)
                || !filePath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(filePath)
                || !IsDownloadedMediaFile(filePath))
            {
                Log($"Rejected downloaded file open request: {fileName}");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });

            Log($"Opened downloaded file: {filePath}");
        }
        catch (Exception ex)
        {
            Log($"Open downloaded file failed: {ex}");
        }
    }

    private static bool IsDownloadedMediaFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);

        return DownloadMediaExtensions.Any(mediaExtension =>
            string.Equals(extension, mediaExtension, StringComparison.OrdinalIgnoreCase));
    }

    public static void ShowReadyMessage()
    {
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        Forms.MessageBox.Show(
            "DLP is ready. Use Download with DLP from a supported browser page.",
            "DLP",
            Forms.MessageBoxButtons.OK,
            Forms.MessageBoxIcon.Information);
    }

    private static void ShowDownloadWindow(
        string url,
        string? audioUrl,
        string? fallbackUrl,
        string source,
        string? title,
        string? referer,
        string? userAgent,
        string? cookieBrowser)
    {
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);

        bool ownsApplication = System.Windows.Application.Current is null;
        System.Windows.Application application = System.Windows.Application.Current ?? new System.Windows.Application();
        application.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;

        DownloadWindow window = new(
            url,
            audioUrl,
            fallbackUrl,
            source,
            title,
            referer,
            userAgent,
            cookieBrowser);

        ActiveWindow = window;

        try
        {
            if (ownsApplication)
            {
                application.Run(window);
                return;
            }

            window.ShowDialog();
        }
        finally
        {
            if (ReferenceEquals(ActiveWindow, window))
            {
                ActiveWindow = null;
            }
        }
    }

    public static void Log(string message)
    {
        DlpLogger.Write("APP", message);
    }
}
internal static class DlpLogger
{
    private const string LogFileName = "DLP.log";
    private static readonly object SyncRoot = new();
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Http403Regex = new(@"(?:\bstatus(?:\s+code)?\s*[:=]?\s*403\b|\bhttp\s*403\b|\b403\s+forbidden\b|\bforbidden\b)", RegexOptions.Compiled);
    private static readonly Regex Http404Regex = new(@"(?:\bstatus(?:\s+code)?\s*[:=]?\s*404\b|\bhttp\s*404\b|\b404\s+not\s+found\b|\bnot\s+found\b)", RegexOptions.Compiled);

    public static string LogPath => Path.Combine(GetLogDirectory(), LogFileName);

    public static void Write(string component, string message)
    {
        try
        {
            LogClassification classification = Classify(message);
            string safeComponent = NormalizeToken(component, "APP");
            string safeMessage = NormalizeMessage(message);
            string line = string.Join(
                " | ",
                DateTimeOffset.UtcNow.ToString("O"),
                classification.Level,
                safeComponent,
                classification.Code,
                safeMessage) + Environment.NewLine;

            lock (SyncRoot)
            {
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never break the app or native messaging protocol.
        }
    }

    private static string GetLogDirectory()
    {
        string installLogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

        try
        {
            Directory.CreateDirectory(installLogDirectory);
            return installLogDirectory;
        }
        catch
        {
            string fallbackDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DLP",
                "logs");

            Directory.CreateDirectory(fallbackDirectory);
            return fallbackDirectory;
        }
    }

    private static LogClassification Classify(string message)
    {
        string text = message.ToLowerInvariant();

        if (text.Contains("yt-dlp.exe was not found", StringComparison.Ordinal))
        {
            return Error("missing_yt_dlp");
        }

        if (text.Contains("ffmpeg.exe was not found", StringComparison.Ordinal))
        {
            return Error("missing_ffmpeg");
        }

        if (text.Contains("drm", StringComparison.Ordinal) || text.Contains("encrypted", StringComparison.Ordinal))
        {
            return Error("encrypted_stream");
        }

        if (text.StartsWith("yt-dlp failure summary", StringComparison.Ordinal))
        {
            return Error("yt_dlp_failure_summary");
        }

        if (text.StartsWith("yt-dlp context", StringComparison.Ordinal))
        {
            return Info("yt_dlp_context");
        }

        if (Http403Regex.IsMatch(text))
        {
            return Error("http_forbidden");
        }

        if (Http404Regex.IsMatch(text))
        {
            return Error("http_not_found");
        }

        if (text.Contains("unsupported_action", StringComparison.Ordinal))
        {
            return Warn("unsupported_action");
        }

        if (text.Contains("invalid_request", StringComparison.Ordinal))
        {
            return Warn("invalid_request");
        }

        if (text.Contains("fallback unavailable", StringComparison.Ordinal))
        {
            return Warn("unsupported_media_url");
        }

        if (text.Contains("duplicate", StringComparison.Ordinal) || text.Contains("already downloaded", StringComparison.Ordinal))
        {
            return Info("duplicate_found");
        }

        if (text.StartsWith("yt-dlp:", StringComparison.Ordinal) || text.StartsWith("yt-dlp update:", StringComparison.Ordinal))
        {
            return text.Contains("error", StringComparison.Ordinal)
                ? Error("yt_dlp_error")
                : Info("yt_dlp_output");
        }

        if (text.Contains("direct attempt failed", StringComparison.Ordinal))
        {
            return Warn("direct_attempt_failed");
        }

        if (text.Contains("direct redirect resolved", StringComparison.Ordinal))
        {
            return Info("direct_redirect");
        }

        if (text.Contains("built-in media fallback failed", StringComparison.Ordinal)
            || text.Contains("direct download failed", StringComparison.Ordinal))
        {
            return Error("direct_download_failed");
        }

        if (text.Contains("download completed", StringComparison.Ordinal)
            || text.Contains("file saved", StringComparison.Ordinal)
            || text.Contains("updated", StringComparison.Ordinal))
        {
            return Info("completed");
        }

        if (text.Contains("received url", StringComparison.Ordinal))
        {
            return Info("request_received");
        }

        if (text.Contains("starting", StringComparison.Ordinal))
        {
            return Info("started");
        }

        if (text.Contains("declined by user", StringComparison.Ordinal)
            || text.Contains("canceled by user", StringComparison.Ordinal))
        {
            return Info("user_canceled");
        }

        if (text.Contains("protocol", StringComparison.Ordinal))
        {
            return Error("native_protocol_error");
        }

        if (text.Contains("failed", StringComparison.Ordinal)
            || text.Contains("error", StringComparison.Ordinal)
            || text.Contains("exception", StringComparison.Ordinal)
            || text.Contains("rejected", StringComparison.Ordinal))
        {
            return Error("error");
        }

        return Info("general");
    }

    private static string NormalizeToken(string value, string fallback)
    {
        string token = WhitespaceRegex.Replace(value.Trim(), "_").ToUpperInvariant();
        return string.IsNullOrWhiteSpace(token) ? fallback : token;
    }

    private static string NormalizeMessage(string message)
    {
        string normalized = WhitespaceRegex.Replace(message.Trim(), " ");

        if (normalized.Length > 4000)
        {
            normalized = normalized[..4000] + " ...";
        }

        return normalized;
    }

    private static LogClassification Info(string code) => new("INFO", code);

    private static LogClassification Warn(string code) => new("WARN", code);

    private static LogClassification Error(string code) => new("ERROR", code);

    private readonly record struct LogClassification(string Level, string Code);
}

internal static class NativeMessagingHost
{
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MediaExtensions =
    [
        ".mp4",
        ".mkv",
        ".webm",
        ".mov",
        ".mp3",
        ".m4a",
        ".opus",
        ".wav",
        ".flac",
        ".aac"
    ];

    private static readonly string[] AudioExtensions =
    [
        ".mp3",
        ".m4a",
        ".opus",
        ".wav",
        ".flac",
        ".aac"
    ];
    public static bool IsNativeMessagingInvocation()
    {
        try
        {
            return Console.IsInputRedirected && Console.IsOutputRedirected;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<int> RunAsync()
    {
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();

        while (true)
        {
            byte[]? messageBytes;

            try
            {
                messageBytes = await ReadNativeMessageAsync(input);
            }
            catch (NativeHostException ex)
            {
                Log($"Protocol error: {ex.ErrorCode}: {ex.Message}");
                await WriteNativeMessageAsync(output, Error(ex.ErrorCode, ex.Message));
                return 1;
            }
            catch (Exception ex)
            {
                Log($"Fatal protocol error: {ex}");
                await WriteNativeMessageAsync(output, Error("protocol_error", "Invalid native messaging input"));
                return 1;
            }

            if (messageBytes is null)
            {
                return 0;
            }

            object response;

            try
            {
                response = HandleMessage(messageBytes);
            }
            catch (NativeHostException ex)
            {
                Log($"Request rejected: {ex.ErrorCode}: {ex.Message}");
                response = Error(ex.ErrorCode, ex.Message);
            }
            catch (Exception ex)
            {
                Log($"Unhandled request error: {ex}");
                response = Error("internal_error", "DLP failed to process the browser request");
            }

            await WriteNativeMessageAsync(output, response);
        }
    }

    private static object HandleMessage(byte[] messageBytes)
    {
        using JsonDocument document = JsonDocument.Parse(messageBytes);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new NativeHostException("invalid_request", "Native message must be a JSON object");
        }

        string action = ReadString(root, "action", required: true)!;

        return action switch
        {
            "ping" => new
            {
                ok = true,
                action = "ping",
                message = "DLP is alive"
            },
            "open_app" => HandleOpenApp(),
            "open_folder" => HandleOpenFolder(),
            "download_folder_status" => HandleDownloadFolderStatus(),
            "unlock_download_folder" => HandleUnlockDownloadFolder(root),
            "lock_download_folder" => HandleLockDownloadFolder(),
            "list_downloads" => HandleListDownloads(),
            "open_download" => HandleOpenDownload(root),
            "open_stream" => HandleOpenStream(root),
            "download" => HandleDownload(root),
            _ => throw new NativeHostException("unsupported_action", "Unsupported native host action")
        };
    }

    private static object HandleDownload(JsonElement root)
    {
        string requestedUrl = ReadString(root, "url", required: true)!;
        string? title = ReadString(root, "title", required: false);
        string? referer = Program.NormalizeOptionalHttpsUrl(FirstNonWhiteSpace(
            ReadString(root, "pageUrl", required: false),
            ReadString(root, "referer", required: false)));
        string? userAgent = Program.NormalizeHeaderValue(ReadString(root, "userAgent", required: false), 512);
        string? cookieBrowser = ReadBoolean(root, "browserCookies", defaultValue: false)
            ? Program.NormalizeCookieBrowser(ReadString(root, "cookieBrowser", required: false))
            : null;
        bool silent = ReadBoolean(root, "silent", defaultValue: false);
        bool experimental = ReadBoolean(root, "experimental", defaultValue: false);
        string normalizedUrl = ValidateAndNormalizeUrl(requestedUrl, experimental);
        string? normalizedAudioUrl = NormalizeOptionalNativeUrl(ReadString(root, "audioUrl", required: false), experimental);
        string fallbackContextUrl = referer ?? normalizedUrl;
        string? normalizedFallbackUrl = NormalizeOptionalNativeFallbackUrl(
            ReadString(root, "fallbackUrl", required: false),
            fallbackContextUrl,
            experimental);
        string appPath = ResolveCurrentAppPath();

        Log("Native download request normalized: "
            + $"requestedUrl={requestedUrl.Trim()} "
            + $"normalizedUrl={normalizedUrl} "
            + $"referer={referer ?? "none"} "
            + $"userAgent={(!string.IsNullOrWhiteSpace(userAgent) ? "present" : "none")} "
            + $"audioUrl={normalizedAudioUrl ?? "none"} "
            + $"fallbackUrl={normalizedFallbackUrl ?? "none"} "
            + $"browserCookies={cookieBrowser ?? "none"} "
            + $"silent={silent} "
            + $"experimental={experimental}");

        ProcessStartInfo startInfo = new()
        {
            FileName = appPath,
            UseShellExecute = false,
            CreateNoWindow = silent,
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? AppContext.BaseDirectory
        };

        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add("browser");
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add(normalizedUrl);

        if (normalizedAudioUrl is not null)
        {
            startInfo.ArgumentList.Add("--audio-url");
            startInfo.ArgumentList.Add(normalizedAudioUrl);
        }

        if (normalizedFallbackUrl is not null)
        {
            startInfo.ArgumentList.Add("--fallback-url");
            startInfo.ArgumentList.Add(normalizedFallbackUrl);
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            startInfo.ArgumentList.Add("--title");
            startInfo.ArgumentList.Add(title.Trim());
        }

        if (referer is not null)
        {
            startInfo.ArgumentList.Add("--referer");
            startInfo.ArgumentList.Add(referer);
        }

        if (userAgent is not null)
        {
            startInfo.ArgumentList.Add("--user-agent");
            startInfo.ArgumentList.Add(userAgent);
        }

        if (cookieBrowser is not null)
        {
            startInfo.ArgumentList.Add("--browser-cookies");
            startInfo.ArgumentList.Add(cookieBrowser);
        }

        if (silent)
        {
            startInfo.ArgumentList.Add("--silent");
        }

        using Process? process = Process.Start(startInfo);

        if (process is null)
        {
            throw new NativeHostException("launch_failed", "DLP could not be opened");
        }

        Log(silent
            ? $"Started silent DLP download for URL: {normalizedUrl} audioPair={normalizedAudioUrl is not null} capturedFallback={normalizedFallbackUrl is not null} experimental={experimental}"
            : $"Opened DLP window for URL: {normalizedUrl} audioPair={normalizedAudioUrl is not null} capturedFallback={normalizedFallbackUrl is not null} experimental={experimental}");

        return new
        {
            ok = true,
            action = "download",
            launched = true,
            silent,
            experimental
        };
    }

    private static object HandleOpenStream(JsonElement root)
    {
        string requestedUrl = ReadString(root, "url", required: true)!;
        string? title = ReadString(root, "title", required: false);
        string? referer = Program.NormalizeOptionalHttpsUrl(FirstNonWhiteSpace(
            ReadString(root, "pageUrl", required: false),
            ReadString(root, "referer", required: false)));
        string? userAgent = Program.NormalizeHeaderValue(ReadString(root, "userAgent", required: false), 512);
        bool experimental = ReadBoolean(root, "experimental", defaultValue: true);
        string normalizedUrl = ValidateAndNormalizeUrl(requestedUrl, experimental);
        string appPath = ResolveCurrentAppPath();

        ProcessStartInfo startInfo = new()
        {
            FileName = appPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? AppContext.BaseDirectory
        };

        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add("browser-stream");
        startInfo.ArgumentList.Add("--stream-url");
        startInfo.ArgumentList.Add(normalizedUrl);

        if (!string.IsNullOrWhiteSpace(title))
        {
            startInfo.ArgumentList.Add("--title");
            startInfo.ArgumentList.Add(title.Trim());
        }

        if (referer is not null)
        {
            startInfo.ArgumentList.Add("--referer");
            startInfo.ArgumentList.Add(referer);
        }

        if (userAgent is not null)
        {
            startInfo.ArgumentList.Add("--user-agent");
            startInfo.ArgumentList.Add(userAgent);
        }

        using Process? process = Process.Start(startInfo);

        if (process is null)
        {
            throw new NativeHostException("launch_failed", "DLP could not open the live stream");
        }

        Log($"Opened live stream proxy for URL: {normalizedUrl}");

        return new
        {
            ok = true,
            action = "open_stream",
            launched = true
        };
    }

    private static object HandleOpenApp()
    {
        string appPath = ResolveCurrentAppPath();
        ProcessStartInfo startInfo = new()
        {
            FileName = appPath,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? AppContext.BaseDirectory
        };

        startInfo.ArgumentList.Add("--open-app");

        using Process? process = Process.Start(startInfo);

        if (process is null)
        {
            throw new NativeHostException("launch_failed", "DLP could not be opened");
        }

        Log("Opened DLP app");

        return new
        {
            ok = true,
            action = "open_app",
            launched = true
        };
    }

    private static object HandleDownloadFolderStatus()
    {
        return new
        {
            ok = true,
            action = "download_folder_status",
            folderAccess = Crypt.GetStatus()
        };
    }

    private static object HandleUnlockDownloadFolder(JsonElement root)
    {
        bool open = ReadBoolean(root, "open", defaultValue: false);
        CryptStatus folderAccess = Crypt.UnlockForCurrentUser();

        if (open && folderAccess.IsSupported)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folderAccess.Directory,
                UseShellExecute = true
            });
        }

        return new
        {
            ok = folderAccess.IsSupported,
            action = "unlock_download_folder",
            opened = open,
            folderAccess,
            message = folderAccess.Message
        };
    }

    private static object HandleLockDownloadFolder()
    {
        CryptStatus folderAccess = Crypt.LockForCurrentUser();

        return new
        {
            ok = folderAccess.IsSupported && !folderAccess.HasActiveOperations,
            action = "lock_download_folder",
            folderAccess,
            message = folderAccess.Message
        };
    }

    private static object HandleListDownloads()
    {
        CryptStatus initialFolderAccess = Crypt.GetStatus();
        using CryptAccessScope readAccess = Crypt.BeginOperationAccess(
            "list-downloads",
            CryptAccessMode.Read);
        string downloadDirectory = readAccess.DirectoryPath;
        bool exposeFileUrls = initialFolderAccess.IsUnlocked;

        var files = Directory.EnumerateFiles(downloadDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsMediaFile)
            .Select(filePath =>
            {
                FileInfo file = new(filePath);

                return new
                {
                    fileName = file.Name,
                    title = GetDisplayTitle(file.Name),
                    extension = file.Extension.TrimStart('.').ToUpperInvariant(),
                    mediaType = IsAudioFile(file.FullName) ? "audio" : "video",
                    fileUrl = exposeFileUrls ? new Uri(file.FullName).AbsoluteUri : null,
                    sizeBytes = file.Length,
                    modified = file.LastWriteTimeUtc.ToString("O")
                };
            })
            .OrderByDescending(file => file.modified)
            .Take(200)
            .ToArray();

        return new
        {
            ok = true,
            action = "list_downloads",
            folderAccess = initialFolderAccess,
            files
        };
    }

    private static object HandleOpenDownload(JsonElement root)
    {
        string fileName = ReadString(root, "fileName", required: true)!;
        Crypt.UnlockForCurrentUser();
        string filePath = ResolveDownloadedMediaPath(fileName);
        string appPath = ResolveCurrentAppPath();

        ProcessStartInfo startInfo = new()
        {
            FileName = appPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? AppContext.BaseDirectory
        };

        startInfo.ArgumentList.Add("--open-download");
        startInfo.ArgumentList.Add(Path.GetFileName(filePath));

        using Process? process = Process.Start(startInfo);

        if (process is null)
        {
            throw new NativeHostException("launch_failed", "DLP could not open the downloaded file");
        }

        Log($"Opened downloaded file through app: {filePath}");

        return new
        {
            ok = true,
            action = "open_download",
            launched = true
        };
    }

    private static object HandleOpenFolder()
    {
        string appPath = ResolveCurrentAppPath();
        ProcessStartInfo startInfo = new()
        {
            FileName = appPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? AppContext.BaseDirectory
        };

        startInfo.ArgumentList.Add("--open-downloads");

        using Process? process = Process.Start(startInfo);

        if (process is null)
        {
            throw new NativeHostException("launch_failed", "DLP download folder could not be opened");
        }

        Log("Opened DLP download folder through app");

        return new
        {
            ok = true,
            action = "open_folder",
            launched = true
        };
    }

    private static string ValidateAndNormalizeUrl(string url, bool experimental)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new NativeHostException("invalid_url", "URL is required");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
        {
            throw new NativeHostException("invalid_url", "URL must be a valid absolute URL");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new NativeHostException("invalid_scheme", "Only HTTPS URLs are allowed");
        }

        bool hostAllowed = YtDlpSites.IsAllowedHost(uri);

        if (!hostAllowed && !experimental)
        {
            throw new NativeHostException("host_not_allowed", "Only supported video sites are allowed");
        }

        return uri.AbsoluteUri;
    }

    private static string ResolveDownloadedMediaPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
        {
            throw new NativeHostException("invalid_file", "Invalid downloaded file name");
        }

        string downloadDirectory = Path.GetFullPath(Program.GetDownloadDirectory());
        string filePath = Path.GetFullPath(Path.Combine(downloadDirectory, fileName));
        string directoryPrefix = downloadDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!filePath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new NativeHostException("invalid_file", "Downloaded file must be inside the DLP download folder");
        }

        if (!File.Exists(filePath) || !IsMediaFile(filePath))
        {
            throw new NativeHostException("file_not_found", "Downloaded media file was not found");
        }

        return filePath;
    }

    private static bool IsMediaFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);

        return MediaExtensions.Any(mediaExtension =>
            string.Equals(extension, mediaExtension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAudioFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);

        return AudioExtensions.Any(audioExtension =>
            string.Equals(extension, audioExtension, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetDisplayTitle(string fileName)
    {
        string title = Path.GetFileNameWithoutExtension(fileName);
        title = Regex.Replace(title, @"\s\[[^\]]+\](?:\s+copy-\d{8}-\d{6})?$", "", RegexOptions.IgnoreCase);

        return string.IsNullOrWhiteSpace(title) ? fileName : title.Trim();
    }

    private static string ResolveCurrentAppPath()
    {
        string? processPath = Environment.ProcessPath;

        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            return processPath;
        }

        string fallback = Path.Combine(AppContext.BaseDirectory, "DLP.exe");

        if (File.Exists(fallback))
        {
            return fallback;
        }

        throw new NativeHostException("app_not_found", "DLP executable was not found");
    }

    private static string? ReadString(JsonElement root, string propertyName, bool required)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            if (required)
            {
                throw new NativeHostException("missing_field", $"Missing required field: {propertyName}.");
            }

            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new NativeHostException("invalid_field", $"Field must be a string: {propertyName}.");
        }

        return value.GetString();
    }

    private static string? NormalizeOptionalNativeUrl(string? url, bool experimental)
    {
        return string.IsNullOrWhiteSpace(url)
            ? null
            : ValidateAndNormalizeUrl(url, experimental);
    }

    private static string? NormalizeOptionalNativeFallbackUrl(string? url, string contextUrl, bool experimental)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
        {
            throw new NativeHostException("invalid_url", "Fallback URL must be a valid absolute URL");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new NativeHostException("invalid_scheme", "Only HTTPS fallback URLs are allowed");
        }

        if (!experimental && !YtDlpPlatformPolicy.ShouldPreferYtDlp(contextUrl, null))
        {
            throw new NativeHostException("host_not_allowed", "Captured fallback URLs require a supported media page");
        }

        if (!YtDlpSites.LooksLikeDirectMediaUrl(uri))
        {
            throw new NativeHostException("invalid_url", "Captured fallback URL must be a direct media URL");
        }

        return uri.AbsoluteUri;
    }

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool ReadBoolean(JsonElement root, string propertyName, bool defaultValue)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
        {
            throw new NativeHostException("invalid_field", $"Field must be a boolean: {propertyName}.");
        }

        return value.GetBoolean();
    }

    private static async Task<byte[]?> ReadNativeMessageAsync(Stream input)
    {
        byte[] lengthBuffer = new byte[4];
        int lengthBytesRead = await ReadExactOrEndAsync(input, lengthBuffer);

        if (lengthBytesRead == 0)
        {
            return null;
        }

        if (lengthBytesRead != lengthBuffer.Length)
        {
            throw new NativeHostException("protocol_error", "Incomplete native message length");
        }

        uint messageLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);

        if (messageLength == 0)
        {
            throw new NativeHostException("protocol_error", "Native message body cannot be empty");
        }

        if (messageLength > MaxMessageBytes)
        {
            throw new NativeHostException("message_too_large", "Native message is too large");
        }

        byte[] messageBuffer = new byte[messageLength];
        int bodyBytesRead = await ReadExactOrEndAsync(input, messageBuffer);

        if (bodyBytesRead != messageBuffer.Length)
        {
            throw new NativeHostException("protocol_error", "Incomplete native message body");
        }

        return messageBuffer;
    }

    private static async Task<int> ReadExactOrEndAsync(Stream input, byte[] buffer)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await input.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));

            if (read == 0)
            {
                return offset;
            }

            offset += read;
        }

        return offset;
    }

    private static async Task WriteNativeMessageAsync(Stream output, object response)
    {
        byte[] responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        byte[] lengthBuffer = new byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(lengthBuffer, (uint)responseBytes.Length);

        await output.WriteAsync(lengthBuffer);
        await output.WriteAsync(responseBytes);
        await output.FlushAsync();
    }

    private static object Error(string error, string message) => new
    {
        ok = false,
        error,
        message
    };

    private static void Log(string message)
    {
        DlpLogger.Write("HOST", message);
    }

    private sealed class NativeHostException : Exception
    {
        public NativeHostException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public string ErrorCode { get; }
    }
}

internal static class DirectMediaPairDownloader
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static async Task<int> DownloadAndMergeAsync(
        string videoUrl,
        string audioUrl,
        string downloadDirectory,
        string? title,
        string ytDlpPath,
        string ffmpegPath,
        string? referer,
        string? userAgent,
        string? cookieBrowser,
        bool createDuplicateCopy,
        Action<string> log,
        Action<string, int>? statusChanged,
        Action<Process?>? processChanged)
    {
        string tempDirectory = Path.Combine(downloadDirectory, ".dlp-temp", Guid.NewGuid().ToString("N"));
        string videoPath = Path.Combine(tempDirectory, "video.mp4");
        string audioPath = Path.Combine(tempDirectory, "audio.mp4");
        string outputPath = BuildOutputPath(downloadDirectory, title, videoUrl, createDuplicateCopy);
        string? normalizedCookieBrowser = CookieBrowserCatalog.Normalize(cookieBrowser);

        Directory.CreateDirectory(tempDirectory);

        try
        {
            if (normalizedCookieBrowser is not null)
            {
                log($"Using browser cookies for paired media: {normalizedCookieBrowser}");
            }

            statusChanged?.Invoke("Downloading video stream", 15);
            int videoExitCode = await RunYtDlpDirectAsync(
                ytDlpPath,
                videoUrl,
                videoPath,
                tempDirectory,
                referer,
                userAgent,
                normalizedCookieBrowser,
                log,
                processChanged);

            if (videoExitCode != 0)
            {
                log($"Paired media video stream failed with exit code {videoExitCode}");
                return videoExitCode;
            }

            statusChanged?.Invoke("Downloading audio stream", 50);
            int audioExitCode = await RunYtDlpDirectAsync(
                ytDlpPath,
                audioUrl,
                audioPath,
                tempDirectory,
                referer,
                userAgent,
                normalizedCookieBrowser,
                log,
                processChanged);

            if (audioExitCode != 0)
            {
                log($"Paired media audio stream failed with exit code {audioExitCode}");
                return audioExitCode;
            }

            statusChanged?.Invoke("Merging audio and video", 85);
            int mergeExitCode = await RunFfmpegMergeAsync(
                ffmpegPath,
                videoPath,
                audioPath,
                outputPath,
                tempDirectory,
                log,
                processChanged);

            if (mergeExitCode != 0)
            {
                log($"Paired media merge failed with exit code {mergeExitCode}");
                return mergeExitCode;
            }

            statusChanged?.Invoke("Done - saved", 100);
            log($"Paired media download completed: {outputPath}");
            return 0;
        }
        finally
        {
            processChanged?.Invoke(null);
            TryDeleteDirectory(tempDirectory, log);
        }
    }

    private static async Task<int> RunYtDlpDirectAsync(
        string ytDlpPath,
        string url,
        string outputPath,
        string workingDirectory,
        string? referer,
        string? userAgent,
        string? cookieBrowser,
        Action<string> log,
        Action<Process?>? processChanged)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = ytDlpPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("--newline");
        startInfo.ArgumentList.Add("--no-playlist");
        startInfo.ArgumentList.Add("--no-mtime");
        startInfo.ArgumentList.Add("--windows-filenames");
        YtDlpNetworkArgumentBuilder.AddNetworkArguments(startInfo, referer, userAgent);
        YtDlpCookieArgumentBuilder.AddCookieArguments(startInfo, cookieBrowser, url);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add(url);

        return await RunProcessAsync(startInfo, "yt-dlp", log, processChanged);
    }

    private static async Task<int> RunFfmpegMergeAsync(
        string ffmpegPath,
        string videoPath,
        string audioPath,
        string outputPath,
        string workingDirectory,
        Action<string> log,
        Action<Process?>? processChanged)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpegPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(audioPath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("1:a:0");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-shortest");
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+faststart");
        startInfo.ArgumentList.Add(outputPath);

        return await RunProcessAsync(startInfo, "ffmpeg", log, processChanged);
    }

    private static async Task<int> RunProcessAsync(
        ProcessStartInfo startInfo,
        string toolName,
        Action<string> log,
        Action<Process?>? processChanged)
    {
        using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        YtDlpRunDiagnostics? ytDlpDiagnostics = string.Equals(toolName, "yt-dlp", StringComparison.OrdinalIgnoreCase)
            ? YtDlpRunDiagnostics.FromProcessStartInfo(startInfo)
            : null;

        ytDlpDiagnostics?.LogStart(startInfo.FileName, log, startInfo);

        process.OutputDataReceived += (_, e) =>
        {
            if (ytDlpDiagnostics is not null)
            {
                ytDlpDiagnostics.LogLine(e.Data, log);
                return;
            }

            LogToolLine(toolName, e.Data, log);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (ytDlpDiagnostics is not null)
            {
                ytDlpDiagnostics.LogLine(e.Data, log);
                return;
            }

            LogToolLine(toolName, e.Data, log);
        };

        processChanged?.Invoke(process);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            ytDlpDiagnostics?.LogFailure(process.ExitCode, log);
        }

        return process.ExitCode;
    }

    private static void LogToolLine(string toolName, string? line, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            log($"{toolName}: {line}");
        }
    }

    private static string BuildOutputPath(string downloadDirectory, string? title, string videoUrl, bool createDuplicateCopy)
    {
        string baseName = BuildSafeBaseName(title, videoUrl);

        if (createDuplicateCopy)
        {
            baseName = $"{baseName} copy-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        }

        string outputPath = Path.Combine(downloadDirectory, $"{baseName}.mp4");

        if (!File.Exists(outputPath))
        {
            return outputPath;
        }

        return Path.Combine(downloadDirectory, $"{baseName} copy-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.mp4");
    }

    private static string BuildSafeBaseName(string? title, string videoUrl)
    {
        string value = !string.IsNullOrWhiteSpace(title)
            ? title.Trim()
            : GetUrlFileStem(videoUrl);

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
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            ? Path.GetFileNameWithoutExtension(uri.AbsolutePath)
            : "DLP media";
    }

    private static void TryDeleteDirectory(string directory, Action<string> log)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            log($"Temporary media cleanup failed: {ex.Message}");
        }
    }
}

internal static class SilentDownloader
{
    public static async Task<int> DownloadVideoAsync(
        string url,
        string? audioUrl,
        string? fallbackUrl,
        string source,
        string? title,
        string? referer,
        string? userAgent,
        string? cookieBrowser)
    {
        using CryptAccessScope folderAccess = Crypt.BeginOperationAccess(
            "silent-download",
            CryptAccessMode.Modify);
        string downloadDirectory = folderAccess.DirectoryPath;
        string? ytDlpPath = ToolResolver.ResolveToolPath("DLP_YTDLP_PATH", "yt-dlp.exe");
        string? ffmpegPath = ToolResolver.ResolveToolPath("DLP_FFMPEG_PATH", "ffmpeg.exe");

        Directory.CreateDirectory(downloadDirectory);
        Program.Log($"Starting silent video download from {source}: {url}");

        if (TitleDuplicateDetector.TryFindExistingDownload(downloadDirectory, title, out string? existingFilePath))
        {
            Program.Log($"Silent download skipped existing title '{title}': {existingFilePath}");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(audioUrl)
            && (!YtDlpPlatformPolicy.ShouldPreferYtDlp(url, referer) || Instagram.IsDirectMediaUrl(url))
            && BuiltInMediaDownloader.CanDownload(url, null))
        {
            try
            {
                Program.Log($"Starting silent built-in media download from {source}: {url}");
                bool directOk = await BuiltInMediaDownloader.DownloadAsync(
                    url,
                    null,
                    downloadDirectory,
                    title,
                    referer,
                    userAgent,
                    ffmpegPath,
                    createDuplicateCopy: false,
                    Program.Log,
                    null,
                    null);

                if (directOk)
                {
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Program.Log($"Silent built-in media download failed before yt-dlp: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(audioUrl)
            && fallbackUrl is not null
            && string.Equals(YtDlpSites.DetectPlatform(url, referer), Instagram.DisplayName, StringComparison.Ordinal)
            && BuiltInMediaDownloader.CanDownload(fallbackUrl, null))
        {
            try
            {
                Program.Log($"Trying captured Instagram media before yt-dlp: {fallbackUrl}");
                bool capturedDirectOk = await BuiltInMediaDownloader.DownloadAsync(
                    fallbackUrl,
                    null,
                    downloadDirectory,
                    title,
                    referer,
                    userAgent,
                    ffmpegPath,
                    createDuplicateCopy: false,
                    Program.Log,
                    null,
                    null);

                if (capturedDirectOk)
                {
                    return 0;
                }

                Program.Log("Captured Instagram media failed before yt-dlp; continuing with yt-dlp attempts.");
            }
            catch (Exception ex)
            {
                Program.Log($"Captured Instagram media failed before yt-dlp: {ex.Message}");
            }
        }

        if (CookieBrowserCatalog.Normalize(cookieBrowser) is string normalizedCookieBrowser)
        {
            Program.Log($"Using browser cookies for yt-dlp: {normalizedCookieBrowser}");
        }

        if (ytDlpPath is null)
        {
            Program.Log("Silent download failed: yt-dlp.exe was not found");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(audioUrl))
        {
            if (ffmpegPath is null)
            {
                Program.Log("Silent paired media download failed: ffmpeg.exe was not found");
                return 1;
            }

            try
            {
                int pairExitCode = await DirectMediaPairDownloader.DownloadAndMergeAsync(
                    url,
                    audioUrl,
                    downloadDirectory,
                    title,
                    ytDlpPath,
                    ffmpegPath,
                    referer,
                    userAgent,
                    cookieBrowser,
                    createDuplicateCopy: false,
                    Program.Log,
                    null,
                    null);

                if (pairExitCode == 0)
                {
                    return 0;
                }

                if (BuiltInMediaDownloader.CanDownload(url, audioUrl))
                {
                    Program.Log("Trying built-in paired media download after yt-dlp failure");
                    bool fallbackOk = await BuiltInMediaDownloader.DownloadAsync(
                        url,
                        audioUrl,
                        downloadDirectory,
                        title,
                        referer,
                        userAgent,
                        ffmpegPath,
                        createDuplicateCopy: false,
                        Program.Log,
                        null,
                        null);

                    return fallbackOk ? 0 : pairExitCode;
                }

                return pairExitCode;
            }
            catch (Exception ex)
            {
                Program.Log($"Silent paired media download failed: {ex}");
                return 1;
            }
        }

        IReadOnlyList<YtDlpDownloadAttempt> attempts = YtDlpSites.GetDownloadAttempts(
            url,
            referer,
            cookieBrowser);
        int lastExitCode = 1;

        for (int attemptIndex = 0; attemptIndex < attempts.Count; attemptIndex++)
        {
            YtDlpDownloadAttempt attempt = attempts[attemptIndex];
            string? attemptCookieBrowser = attempt.SuppressCookies ? null : cookieBrowser;
            ProcessStartInfo startInfo = new()
            {
                FileName = ytDlpPath,
                WorkingDirectory = downloadDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            AddCommonArguments(startInfo, downloadDirectory, ffmpegPath, referer, userAgent, attemptCookieBrowser, attempt, url);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(attempt.GetVideoFormatSelector("bv*+ba/b"));
            startInfo.ArgumentList.Add("--merge-output-format");
            startInfo.ArgumentList.Add("mp4");
            attempt.AddTo(startInfo);
            startInfo.ArgumentList.Add(url);

            using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            YtDlpRunDiagnostics diagnostics = new(
                url,
                referer,
                userAgent,
                attemptCookieBrowser,
                fallbackUrl);

            Program.Log($"yt-dlp attempt {attemptIndex + 1}/{attempts.Count}: {attempt.Name}");
            diagnostics.LogStart(ytDlpPath, Program.Log, startInfo);
            process.OutputDataReceived += (_, e) => diagnostics.LogLine(e.Data, Program.Log);
            process.ErrorDataReceived += (_, e) => diagnostics.LogLine(e.Data, Program.Log);

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();
                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    Program.Log("Silent video download completed");
                    return 0;
                }

                lastExitCode = process.ExitCode;
                Program.Log($"Silent video download failed with exit code {process.ExitCode}");
                diagnostics.LogFailure(process.ExitCode, Program.Log);

                if (attemptIndex < attempts.Count - 1)
                {
                    Program.Log($"Retrying yt-dlp with attempt: {attempts[attemptIndex + 1].Name}");
                    continue;
                }

                if (fallbackUrl is not null && BuiltInMediaDownloader.CanDownload(fallbackUrl, null))
                {
                    Program.Log($"Trying built-in captured media fallback after yt-dlp failure: {fallbackUrl}");
                    bool capturedFallbackOk = await BuiltInMediaDownloader.DownloadAsync(
                        fallbackUrl,
                        null,
                        downloadDirectory,
                        title,
                        referer,
                        userAgent,
                        ffmpegPath,
                        createDuplicateCopy: false,
                        Program.Log,
                        null,
                        null);

                    return capturedFallbackOk ? 0 : process.ExitCode;
                }

                if (BuiltInMediaDownloader.CanDownload(url, null))
                {
                    Program.Log("Trying built-in media download after yt-dlp failure");
                    bool fallbackOk = await BuiltInMediaDownloader.DownloadAsync(
                        url,
                        null,
                        downloadDirectory,
                        title,
                        referer,
                        userAgent,
                        ffmpegPath,
                        createDuplicateCopy: false,
                        Program.Log,
                        null,
                        null);

                    return fallbackOk ? 0 : process.ExitCode;
                }

                Program.Log($"Built-in media fallback unavailable for silent URL: {url}");
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                Program.Log($"Silent download start failed: {ex}");

                if (attemptIndex < attempts.Count - 1)
                {
                    Program.Log($"Retrying yt-dlp with attempt: {attempts[attemptIndex + 1].Name}");
                    continue;
                }

                return 1;
            }
        }

        return lastExitCode;
    }

    private static void AddCommonArguments(
        ProcessStartInfo startInfo,
        string downloadDirectory,
        string? ffmpegPath,
        string? referer,
        string? userAgent,
        string? cookieBrowser,
        YtDlpDownloadAttempt? attempt,
        string url)
    {
        startInfo.ArgumentList.Add("--newline");
        if (attempt?.AllowPlaylist != true)
        {
            startInfo.ArgumentList.Add("--no-playlist");
        }
        startInfo.ArgumentList.Add("--no-mtime");
        startInfo.ArgumentList.Add("--windows-filenames");
        YtDlpNetworkArgumentBuilder.AddNetworkArguments(startInfo, referer, userAgent);
        YtDlpCookieArgumentBuilder.AddCookieArguments(startInfo, cookieBrowser, url);
        startInfo.ArgumentList.Add("-P");
        startInfo.ArgumentList.Add(downloadDirectory);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("%(title).200B [%(id)s].%(ext)s");

        if (ffmpegPath is not null)
        {
            startInfo.ArgumentList.Add("--ffmpeg-location");
            startInfo.ArgumentList.Add(Path.GetDirectoryName(ffmpegPath) ?? ffmpegPath);
        }
    }

    private static void LogYtDlpLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            Program.Log($"yt-dlp: {YtDlpRunDiagnostics.CleanLine(line)}");
        }
    }
}

internal static class TitleDuplicateDetector
{
    private static readonly Regex MediaIdSuffixRegex = new(@"\s\[[^\]]+\]$", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly string[] TitleSuffixes =
    [
        " - YouTube",
        " | TikTok",
        " | X",
        " / X",
        " on X",
        " | SoundCloud"
    ];

    public static bool TryFindExistingDownload(string downloadDirectory, string? title, out string? existingFilePath)
    {
        existingFilePath = null;
        string normalizedTitle = NormalizeTitle(title);

        if (string.IsNullOrWhiteSpace(normalizedTitle) || !Directory.Exists(downloadDirectory))
        {
            return false;
        }

        foreach (string filePath in Directory.EnumerateFiles(downloadDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(filePath);

            if (IsTemporaryDownloadFile(fileName))
            {
                continue;
            }

            string existingTitle = MediaIdSuffixRegex.Replace(Path.GetFileNameWithoutExtension(fileName), "");

            if (NormalizeTitle(existingTitle) == normalizedTitle)
            {
                existingFilePath = filePath;
                return true;
            }
        }

        return false;
    }

    public static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string cleaned = value.Trim();

        foreach (string suffix in TitleSuffixes)
        {
            if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[..^suffix.Length].Trim();
                break;
            }
        }

        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(invalidCharacter, ' ');
        }

        return WhitespaceRegex.Replace(cleaned, " ").Trim().ToLowerInvariant();
    }

    private static bool IsTemporaryDownloadFile(string fileName)
    {
        return fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".temp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class ToolResolver
{
    public static string? ResolveToolPath(string environmentVariable, string fileName)
    {
        string? environmentPath = Environment.GetEnvironmentVariable(environmentVariable);

        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
        {
            return Path.GetFullPath(environmentPath);
        }

        foreach (string directory in EnumerateSearchDirectories())
        {
            string directCandidate = Path.Combine(directory, fileName);
            string toolsCandidate = Path.Combine(directory, "tools", fileName);

            if (File.Exists(directCandidate))
            {
                return Path.GetFullPath(directCandidate);
            }

            if (File.Exists(toolsCandidate))
            {
                return Path.GetFullPath(toolsCandidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSearchDirectories()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (seen.Add(directory.FullName))
            {
                yield return directory.FullName;
            }

            directory = directory.Parent;
        }

        string currentDirectory = Environment.CurrentDirectory;

        if (seen.Add(currentDirectory))
        {
            yield return currentDirectory;
        }
    }
}

internal enum AppUpdateStatus
{
    Available,
    UpToDate,
    NoInstallerAsset,
    Failed
}

internal sealed record AppUpdateInfo(
    AppUpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    string? InstallerUrl,
    string? InstallerName,
    string? Sha256Digest,
    string? Message);

internal static class AppUpdater
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/IBRHUB/DLP/releases/latest";
    private const string FallbackReleaseApiUrl = "https://api.github.com/repos/IBRHUB/DLP/releases/tags/1.0.1";
    private const string PreferredInstallerName = "DLP_Setup.exe";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<AppUpdateInfo> CheckAsync()
    {
        string currentVersionText = GetCurrentVersionText();

        try
        {
            using HttpClient client = CreateHttpClient();
            GitHubRelease? release = await GetReleaseAsync(client, LatestReleaseApiUrl)
                ?? await GetReleaseAsync(client, FallbackReleaseApiUrl);

            if (release is null)
            {
                return new AppUpdateInfo(
                    AppUpdateStatus.Failed,
                    currentVersionText,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "Could not read GitHub release");
            }

            string latestVersionText = NormalizeVersionText(release.TagName);

            if (TryParseVersion(currentVersionText, out Version? currentVersion)
                && TryParseVersion(latestVersionText, out Version? latestVersion)
                && latestVersion <= currentVersion)
            {
                return new AppUpdateInfo(
                    AppUpdateStatus.UpToDate,
                    currentVersionText,
                    latestVersionText,
                    release.HtmlUrl,
                    null,
                    null,
                    null,
                    "DLP is up to date");
            }

            GitHubAsset? installerAsset = SelectInstallerAsset(release.Assets);

            if (installerAsset is null || string.IsNullOrWhiteSpace(installerAsset.BrowserDownloadUrl))
            {
                return new AppUpdateInfo(
                    AppUpdateStatus.NoInstallerAsset,
                    currentVersionText,
                    latestVersionText,
                    release.HtmlUrl,
                    null,
                    null,
                    null,
                    "Release does not include DLP_Setup.exe");
            }

            return new AppUpdateInfo(
                AppUpdateStatus.Available,
                currentVersionText,
                latestVersionText,
                release.HtmlUrl,
                installerAsset.BrowserDownloadUrl,
                installerAsset.Name,
                NormalizeSha256Digest(installerAsset.Digest),
                null);
        }
        catch (Exception ex)
        {
            Program.Log($"App update check failed: {ex}");
            return new AppUpdateInfo(
                AppUpdateStatus.Failed,
                currentVersionText,
                null,
                null,
                null,
                null,
                null,
                "Update check failed");
        }
    }

    public static async Task<string> DownloadInstallerAsync(AppUpdateInfo updateInfo, Action<int>? reportProgress)
    {
        if (string.IsNullOrWhiteSpace(updateInfo.InstallerUrl))
        {
            throw new InvalidOperationException("Installer URL is missing");
        }

        string updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DLP",
            "updates");

        Directory.CreateDirectory(updateDirectory);

        string installerName = string.IsNullOrWhiteSpace(updateInfo.InstallerName)
            ? PreferredInstallerName
            : Path.GetFileName(updateInfo.InstallerName);

        string installerPath = Path.Combine(updateDirectory, installerName);

        using HttpClient client = CreateHttpClient();
        using HttpResponseMessage response = await client.GetAsync(updateInfo.InstallerUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        await using Stream remoteStream = await response.Content.ReadAsStreamAsync();
        await using FileStream fileStream = new(installerPath, FileMode.Create, FileAccess.Write, FileShare.None);

        byte[] buffer = new byte[1024 * 128];
        long totalRead = 0;
        int lastProgress = -1;

        while (true)
        {
            int read = await remoteStream.ReadAsync(buffer);

            if (read == 0)
            {
                break;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            totalRead += read;

            if (contentLength is > 0)
            {
                int progress = Math.Clamp((int)Math.Round(totalRead * 100d / contentLength.Value), 0, 100);

                if (progress != lastProgress)
                {
                    lastProgress = progress;
                    reportProgress?.Invoke(progress);
                }
            }
        }

        reportProgress?.Invoke(100);

        if (!string.IsNullOrWhiteSpace(updateInfo.Sha256Digest))
        {
            string actualDigest = await ComputeSha256Async(installerPath);

            if (!string.Equals(actualDigest, updateInfo.Sha256Digest, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(installerPath);
                throw new InvalidOperationException("Downloaded installer checksum did not match the release digest");
            }
        }

        return installerPath;
    }

    public static void StartInstaller(string installerPath)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = installerPath,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        startInfo.ArgumentList.Add("/CURRENTUSER");
        startInfo.ArgumentList.Add("/SILENT");
        startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");

        Process.Start(startInfo);
    }

    private static async Task<GitHubRelease?> GetReleaseAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            Program.Log($"GitHub release request failed {response.StatusCode}: {url}");
            return null;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions);
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("DLP/1.0.1 (+https://github.com/IBRHUB/DLP)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        return client;
    }

    private static GitHubAsset? SelectInstallerAsset(IReadOnlyList<GitHubAsset>? assets)
    {
        if (assets is null || assets.Count == 0)
        {
            return null;
        }

        return assets.FirstOrDefault(asset => string.Equals(asset.Name, PreferredInstallerName, StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && asset.Name.Contains("setup", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetCurrentVersionText()
    {
        Assembly assembly = typeof(Program).Assembly;
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return NormalizeVersionText(informationalVersion);
        }

        return NormalizeVersionText(assembly.GetName().Version?.ToString() ?? "0.0.0");
    }

    private static bool TryParseVersion(string versionText, out Version? version)
    {
        return Version.TryParse(NormalizeVersionText(versionText), out version);
    }

    private static string NormalizeVersionText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        string normalized = value.Trim();

        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        int metadataIndex = normalized.IndexOf('+', StringComparison.Ordinal);

        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }

        int prereleaseIndex = normalized.IndexOf('-', StringComparison.Ordinal);

        if (prereleaseIndex >= 0)
        {
            normalized = normalized[..prereleaseIndex];
        }

        return normalized;
    }

    private static string? NormalizeSha256Digest(string? digest)
    {
        const string prefix = "sha256:";

        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return digest[prefix.Length..].Trim();
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        await using FileStream stream = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(stream);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest);
}

internal readonly record struct YtDlpDownloadOptions(
    bool EmbedSubs,
    bool UseCookies,
    string Browser,
    int? QualityHeight,
    string Format,
    string SaveDirectory);

internal static class CookieBrowserCatalog
{
    public static readonly string[] Values =
    [
        "brave",
        "chrome",
        "edge",
        "firefox",
        "opera",
        "vivaldi",
        "chromium",
        "whale"
    ];

    public static string? Normalize(string? browser)
    {
        if (string.IsNullOrWhiteSpace(browser))
        {
            return null;
        }

        string normalized = browser.Trim().ToLowerInvariant();

        foreach (string value in Values)
        {
            if (string.Equals(normalized, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, ToDisplayName(value), StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    public static string ToDisplayName(string browser)
    {
        return browser switch
        {
            "brave" => "Brave",
            "chrome" => "Chrome",
            "edge" => "Edge",
            "firefox" => "Firefox",
            "opera" => "Opera",
            "vivaldi" => "Vivaldi",
            "chromium" => "Chromium",
            "whale" => "Whale",
            _ => string.IsNullOrWhiteSpace(browser)
                ? string.Empty
                : string.Concat(browser[..1].ToUpperInvariant(), browser[1..])
        };
    }
}

internal sealed class YtDlpRunDiagnostics
{
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly object VersionLock = new();
    private static readonly Dictionary<string, string> VersionCache = [];

    private readonly object _sync = new();
    private readonly List<string> _warnings = [];
    private readonly List<string> _errors = [];
    private readonly string _url;
    private readonly string? _referer;
    private readonly string? _userAgent;
    private readonly string? _cookieBrowser;
    private readonly string? _fallbackUrl;

    public YtDlpRunDiagnostics(
        string url,
        string? referer,
        string? userAgent,
        string? cookieBrowser,
        string? fallbackUrl)
    {
        _url = url;
        _referer = referer;
        _userAgent = userAgent;
        _cookieBrowser = CookieBrowserCatalog.Normalize(cookieBrowser);
        _fallbackUrl = fallbackUrl;
    }

    public static YtDlpRunDiagnostics FromProcessStartInfo(ProcessStartInfo startInfo)
    {
        string url = startInfo.ArgumentList.Count > 0
            ? startInfo.ArgumentList[^1]
            : string.Empty;
        string? referer = ReadArgumentValue(startInfo, "--referer");
        string? userAgent = ReadArgumentValue(startInfo, "--user-agent");
        string? cookieBrowser = ReadArgumentValue(startInfo, "--cookies-from-browser");

        return new YtDlpRunDiagnostics(url, referer, userAgent, cookieBrowser, null);
    }

    public static string CleanLine(string line)
    {
        return AnsiEscapeRegex.Replace(line, string.Empty).Replace('\r', ' ').Trim();
    }

    public void LogStart(string ytDlpPath, Action<string> log, ProcessStartInfo? startInfo = null)
    {
        string version = GetYtDlpVersion(ytDlpPath);
        log("yt-dlp context: "
            + $"version={version} "
            + $"platform={YtDlpSites.DetectPlatform(_url, _referer)} "
            + $"url=\"{EscapeForLog(Shorten(_url, 700))}\" "
            + $"urlHost={GetHost(_url)} "
            + $"referer=\"{EscapeForLog(Shorten(_referer ?? "none", 700))}\" "
            + $"refererHost={GetHost(_referer)} "
            + $"browserCookies={_cookieBrowser ?? "none"} "
            + $"userAgent={(!string.IsNullOrWhiteSpace(_userAgent) ? "present" : "none")} "
            + $"capturedFallback={(!string.IsNullOrWhiteSpace(_fallbackUrl) ? "present" : "none")}");

        if (startInfo is not null)
        {
            log($"yt-dlp command: exe=\"{EscapeForLog(Shorten(ytDlpPath, 500))}\" args={FormatArguments(startInfo)}");
        }
    }

    public void LogLine(string? line, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        string cleanLine = CleanLine(line);

        if (string.IsNullOrWhiteSpace(cleanLine))
        {
            return;
        }

        log($"yt-dlp: {cleanLine}");
        string lower = cleanLine.ToLowerInvariant();

        lock (_sync)
        {
            if (lower.Contains("error:", StringComparison.Ordinal))
            {
                _errors.Add(cleanLine);
            }
            else if (lower.Contains("warning:", StringComparison.Ordinal))
            {
                _warnings.Add(cleanLine);
            }
        }
    }

    public void LogFailure(int exitCode, Action<string> log)
    {
        (string reason, string message, string warning) = AnalyzeFailure();
        log("yt-dlp failure summary: "
            + $"exitCode={exitCode} "
            + $"reason={reason} "
            + $"platform={YtDlpSites.DetectPlatform(_url, _referer)} "
            + $"urlHost={GetHost(_url)} "
            + $"refererHost={GetHost(_referer)} "
            + $"browserCookies={_cookieBrowser ?? "none"} "
            + $"userAgent={(!string.IsNullOrWhiteSpace(_userAgent) ? "present" : "none")} "
            + $"capturedFallback={(!string.IsNullOrWhiteSpace(_fallbackUrl) ? "present" : "none")} "
            + $"message=\"{EscapeForLog(message)}\" "
            + $"warning=\"{EscapeForLog(warning)}\"");
    }

    private (string Reason, string Message, string Warning) AnalyzeFailure()
    {
        string[] errors;
        string[] warnings;

        lock (_sync)
        {
            errors = [.. _errors];
            warnings = [.. _warnings];
        }

        string message = errors.LastOrDefault() ?? "No explicit yt-dlp ERROR line was captured.";
        string warning = warnings.LastOrDefault() ?? "";
        string combined = string.Join(" ", errors.Concat(warnings)).ToLowerInvariant();
        string reason = YtDlpSites.ClassifyFailure(combined);

        return (reason, Shorten(CleanErrorPrefix(message), 900), Shorten(CleanWarningPrefix(warning), 500));
    }

    private static string GetYtDlpVersion(string ytDlpPath)
    {
        string key;

        try
        {
            key = Path.GetFullPath(ytDlpPath);
        }
        catch
        {
            key = ytDlpPath;
        }

        lock (VersionLock)
        {
            if (VersionCache.TryGetValue(key, out string? cachedVersion))
            {
                return cachedVersion;
            }
        }

        string version = QueryYtDlpVersion(ytDlpPath);

        lock (VersionLock)
        {
            VersionCache[key] = version;
        }

        return version;
    }

    private static string QueryYtDlpVersion(string ytDlpPath)
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = ytDlpPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("--version");

            using Process process = new() { StartInfo = startInfo };
            process.Start();

            if (!process.WaitForExit(3000))
            {
                TryKill(process);
                return "version_timeout";
            }

            string output = process.StandardOutput.ReadToEnd().Trim();

            if (string.IsNullOrWhiteSpace(output))
            {
                output = process.StandardError.ReadToEnd().Trim();
            }

            return string.IsNullOrWhiteSpace(output)
                ? $"unknown_exit_{process.ExitCode}"
                : Shorten(CleanLine(output), 80);
        }
        catch (Exception ex)
        {
            return $"version_error_{ex.GetType().Name}";
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Diagnostics must never block a download.
        }
    }

    private static string? ReadArgumentValue(ProcessStartInfo startInfo, string name)
    {
        for (int i = 0; i < startInfo.ArgumentList.Count - 1; i++)
        {
            if (string.Equals(startInfo.ArgumentList[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return startInfo.ArgumentList[i + 1];
            }
        }

        return null;
    }

    private static string FormatArguments(ProcessStartInfo startInfo)
    {
        return string.Join(" ", startInfo.ArgumentList.Select(argument =>
            "\"" + EscapeForLog(Shorten(argument, 700)) + "\""));
    }

    private static string GetHost(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            ? uri.Host.ToLowerInvariant()
            : "unknown";
    }

    private static string CleanErrorPrefix(string message)
    {
        int index = message.IndexOf("ERROR:", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? message[(index + "ERROR:".Length)..].Trim() : message.Trim();
    }

    private static string CleanWarningPrefix(string message)
    {
        int index = message.IndexOf("WARNING:", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? message[(index + "WARNING:".Length)..].Trim() : message.Trim();
    }

    private static string EscapeForLog(string value)
    {
        return value.Replace('"', '\'');
    }

    private static string Shorten(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength].Trim() + " ...";
    }
}

internal static class YtDlpNetworkArgumentBuilder
{
    public static void AddNetworkArguments(ProcessStartInfo startInfo, string? referer, string? userAgent)
    {
        if (!string.IsNullOrWhiteSpace(referer))
        {
            startInfo.ArgumentList.Add("--referer");
            startInfo.ArgumentList.Add(referer.Trim());
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            startInfo.ArgumentList.Add("--user-agent");
            startInfo.ArgumentList.Add(userAgent.Trim());
        }
    }
}

internal static class YtDlpCookieArgumentBuilder
{
    public static void AddCookieArguments(ProcessStartInfo startInfo, string? browser, string? url)
    {
        string? normalizedBrowser = CookieBrowserCatalog.Normalize(browser);

        if (normalizedBrowser is null)
        {
            return;
        }

        startInfo.ArgumentList.Add("--cookies-from-browser");
        startInfo.ArgumentList.Add(normalizedBrowser);
    }
}

internal static class YtDlpArgumentBuilder
{
    public static void AddVideoArguments(
        ProcessStartInfo startInfo,
        YtDlpDownloadOptions options,
        string url,
        YtDlpDownloadAttempt? attempt = null)
    {
        string defaultSelector = BuildVideoFormatSelector(options.QualityHeight);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(attempt?.GetVideoFormatSelector(defaultSelector) ?? defaultSelector);
        startInfo.ArgumentList.Add("--merge-output-format");
        startInfo.ArgumentList.Add(NormalizeVideoFormat(options.Format));

        if (options.EmbedSubs)
        {
            startInfo.ArgumentList.Add("--all-subs");
            startInfo.ArgumentList.Add("--embed-subs");
        }

        AddCookieArguments(startInfo, options, url);
    }

    public static void AddAudioArguments(ProcessStartInfo startInfo, YtDlpDownloadOptions options, string url)
    {
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("bestaudio/best");
        startInfo.ArgumentList.Add("-x");
        startInfo.ArgumentList.Add("--audio-format");
        startInfo.ArgumentList.Add(NormalizeAudioFormat(options.Format));
        startInfo.ArgumentList.Add("--audio-quality");
        startInfo.ArgumentList.Add("0");

        AddCookieArguments(startInfo, options, url);
    }

    private static string BuildVideoFormatSelector(int? qualityHeight)
    {
        if (qualityHeight is not > 0)
        {
            return "bv*+ba/b";
        }

        int height = Math.Clamp(qualityHeight.Value, 144, 4320);
        return $"bv*[height<={height}]+ba/b[height<={height}]/best[height<={height}]/bv*+ba/b";
    }

    private static string NormalizeVideoFormat(string? format)
    {
        string normalized = (format ?? "mp4").Trim().ToLowerInvariant();

        return normalized switch
        {
            "mkv" => "mkv",
            "webm" => "webm",
            _ => "mp4"
        };
    }

    private static string NormalizeAudioFormat(string? format)
    {
        string normalized = (format ?? "mp3").Trim().ToLowerInvariant();

        return normalized switch
        {
            "m4a" => "m4a",
            "opus" => "opus",
            "wav" => "wav",
            _ => "mp3"
        };
    }

    private static void AddCookieArguments(ProcessStartInfo startInfo, YtDlpDownloadOptions options, string url)
    {
        if (!options.UseCookies || string.IsNullOrWhiteSpace(options.Browser))
        {
            return;
        }

        YtDlpCookieArgumentBuilder.AddCookieArguments(startInfo, options.Browser, url);
    }
}

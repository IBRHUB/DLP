using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? source = ReadOption(args, "--source");
        string? url = ReadOption(args, "--url");
        bool silent = HasSwitch(args, "--silent");

        if (string.IsNullOrWhiteSpace(url) && NativeMessagingHost.IsNativeMessagingInvocation())
        {
            return NativeMessagingHost.RunAsync().GetAwaiter().GetResult();
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            ApplicationConfiguration.Initialize();
            MessageBox.Show(
                "DLP is ready. Use Download with DLP from a supported browser page.",
                "DLP",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        Log($"Received URL from source '{source ?? "unknown"}': {url}");

        if (silent)
        {
            return SilentDownloader.DownloadVideoAsync(url, source ?? "unknown").GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new DownloadForm(url, source ?? "unknown"));

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

    public static void Log(string message)
    {
        try
        {
            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DLP",
                "logs");

            Directory.CreateDirectory(logDirectory);

            string logPath = Path.Combine(logDirectory, "app.log");
            string line = $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";

            File.AppendAllText(logPath, line, Encoding.UTF8);
        }
        catch
        {
            // The app should not fail just because logging failed.
        }
    }
}

internal static class NativeMessagingHost
{
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] AllowedHosts =
    [
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "youtu.be",
        "tiktok.com",
        "www.tiktok.com",
        "m.tiktok.com",
        "vm.tiktok.com",
        "vt.tiktok.com",
        "instagram.com",
        "www.instagram.com",
        "m.instagram.com",
        "x.com",
        "www.x.com",
        "mobile.x.com",
        "twitter.com",
        "www.twitter.com",
        "mobile.twitter.com",
        "soundcloud.com",
        "www.soundcloud.com",
        "m.soundcloud.com",
        "on.soundcloud.com"
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
            "download" => HandleDownload(root),
            _ => throw new NativeHostException("unsupported_action", "Unsupported native host action")
        };
    }

    private static object HandleDownload(JsonElement root)
    {
        string requestedUrl = ReadString(root, "url", required: true)!;
        bool silent = ReadBoolean(root, "silent", defaultValue: false);
        string normalizedUrl = ValidateAndNormalizeUrl(requestedUrl);
        string appPath = ResolveCurrentAppPath();

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
            ? $"Started silent DLP download for URL: {normalizedUrl}"
            : $"Opened DLP window for URL: {normalizedUrl}");

        return new
        {
            ok = true,
            action = "download",
            launched = true,
            silent
        };
    }

    private static string ValidateAndNormalizeUrl(string url)
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

        bool hostAllowed = AllowedHosts.Any(host =>
            string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase));

        if (!hostAllowed)
        {
            throw new NativeHostException("host_not_allowed", "Only supported video sites are allowed");
        }

        return uri.AbsoluteUri;
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
        try
        {
            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DLP",
                "logs");

            Directory.CreateDirectory(logDirectory);

            string logPath = Path.Combine(logDirectory, "native-host.log");
            string line = $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";

            File.AppendAllText(logPath, line, Encoding.UTF8);
        }
        catch
        {
            // Native Messaging stdout must remain reserved for protocol responses.
        }
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

internal static class SilentDownloader
{
    public static async Task<int> DownloadVideoAsync(string url, string source)
    {
        string downloadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "DLP");

        string? ytDlpPath = ToolResolver.ResolveToolPath("DLP_YTDLP_PATH", "yt-dlp.exe");
        string? ffmpegPath = ToolResolver.ResolveToolPath("DLP_FFMPEG_PATH", "ffmpeg.exe");

        if (ytDlpPath is null)
        {
            Program.Log("Silent download failed: yt-dlp.exe was not found");
            return 1;
        }

        Directory.CreateDirectory(downloadDirectory);
        Program.Log($"Starting silent video download from {source}: {url}");

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

        AddCommonArguments(startInfo, downloadDirectory, ffmpegPath);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("bv*+ba/b");
        startInfo.ArgumentList.Add("--merge-output-format");
        startInfo.ArgumentList.Add("mp4");
        startInfo.ArgumentList.Add(url);

        using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) => LogYtDlpLine(e.Data);
        process.ErrorDataReceived += (_, e) => LogYtDlpLine(e.Data);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                Program.Log("Silent video download completed");
            }
            else
            {
                Program.Log($"Silent video download failed with exit code {process.ExitCode}");
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Program.Log($"Silent download start failed: {ex}");
            return 1;
        }
    }

    private static void AddCommonArguments(ProcessStartInfo startInfo, string downloadDirectory, string? ffmpegPath)
    {
        startInfo.ArgumentList.Add("--newline");
        startInfo.ArgumentList.Add("--no-playlist");
        startInfo.ArgumentList.Add("--no-mtime");
        startInfo.ArgumentList.Add("--windows-filenames");
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
            Program.Log($"yt-dlp: {line}");
        }
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

internal sealed class DownloadForm : Form
{
    private static readonly Regex ProgressRegex = new(@"(?<percent>\d{1,3}(?:\.\d+)?)%", RegexOptions.Compiled);

    private readonly string _url;
    private readonly string _source;
    private readonly string _downloadDirectory;
    private readonly string? _ytDlpPath;
    private readonly string? _ffmpegPath;

    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Button _videoButton = new();
    private readonly Button _audioButton = new();
    private readonly Button _openFolderButton = new();

    private Process? _downloadProcess;

    public DownloadForm(string url, string source)
    {
        _url = url;
        _source = source;
        _downloadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "DLP");
        _ytDlpPath = ToolResolver.ResolveToolPath("DLP_YTDLP_PATH", "yt-dlp.exe");
        _ffmpegPath = ToolResolver.ResolveToolPath("DLP_FFMPEG_PATH", "ffmpeg.exe");

        BuildUi();
        SetReadyState();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        CancelDownload();
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        TopMost = true;
        BringToFront();
        Activate();

        BeginInvoke(new Action(() => TopMost = false));
    }

    private void BuildUi()
    {
        Text = "DLP";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        Width = 600;
        Height = 292;
        BackColor = Color.FromArgb(250, 251, 252);
        Font = new Font("Segoe UI", 10F);

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 22, 24, 18),
            RowCount = 6,
            ColumnCount = 1
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        TableLayoutPanel urlPanel = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 14)
        };

        urlPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        urlPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label urlCaption = new()
        {
            Text = "URL",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(55, 65, 81),
            Margin = new Padding(0, 0, 0, 5)
        };

        TextBox urlBox = new()
        {
            Text = _url,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(31, 41, 55),
            Dock = DockStyle.Top,
            Height = 32,
            Margin = new Padding(0)
        };

        urlPanel.Controls.Add(urlCaption);
        urlPanel.Controls.Add(urlBox);

        TableLayoutPanel folderRow = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 18)
        };

        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label folderLabel = new()
        {
            Text = $"Saves to {_downloadDirectory}",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 36,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(75, 85, 99),
            Margin = new Padding(0, 0, 12, 0)
        };

        ConfigureSecondaryButton(_openFolderButton, "Open folder", (_, _) => OpenDownloadFolder());

        folderRow.Controls.Add(folderLabel, 0, 0);
        folderRow.Controls.Add(_openFolderButton, 1, 0);

        TableLayoutPanel actions = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 16)
        };

        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        ConfigurePrimaryButton(_videoButton, "Download video", async (_, _) => await StartDownloadAsync(DownloadKind.Video));
        ConfigurePrimaryButton(_audioButton, "Download audio", async (_, _) => await StartDownloadAsync(DownloadKind.Audio));

        actions.Controls.Add(_videoButton, 0, 0);
        actions.Controls.Add(_audioButton, 1, 0);

        _progressBar.Dock = DockStyle.Top;
        _progressBar.Height = 10;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Margin = new Padding(0, 0, 0, 12);
        _progressBar.Visible = false;

        _statusLabel.Text = "Ready";
        _statusLabel.AutoSize = false;
        _statusLabel.Dock = DockStyle.Top;
        _statusLabel.Height = 28;
        _statusLabel.ForeColor = Color.FromArgb(44, 52, 62);

        TableLayoutPanel footer = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 5,
            RowCount = 1,
            Margin = new Padding(0, 2, 0, 0)
        };

        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label footerSpacer = new() { AutoSize = true };
        Label footerText = CreateFooterLabel("Developed by ");
        LinkLabel ibrahimLink = CreateFooterLink("IBRAHIM", "https://ibrhub.net");
        Label sourceText = CreateFooterLabel(" | Source ");
        LinkLabel sourceLink = CreateFooterLink("IBRHUB/DLP", "https://github.com/IBRHUB/DLP");

        footer.Controls.Add(footerSpacer, 0, 0);
        footer.Controls.Add(footerText, 1, 0);
        footer.Controls.Add(ibrahimLink, 2, 0);
        footer.Controls.Add(sourceText, 3, 0);
        footer.Controls.Add(sourceLink, 4, 0);

        root.Controls.Add(urlPanel);
        root.Controls.Add(folderRow);
        root.Controls.Add(actions);
        root.Controls.Add(_progressBar);
        root.Controls.Add(_statusLabel);
        root.Controls.Add(footer);

        Controls.Add(root);
    }

    private Label CreateFooterLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.FromArgb(107, 114, 128),
        Font = new Font(Font.FontFamily, 8.5F),
        Margin = new Padding(0)
    };

    private LinkLabel CreateFooterLink(string text, string url)
    {
        LinkLabel link = new()
        {
            Text = text,
            AutoSize = true,
            LinkColor = Color.FromArgb(22, 101, 216),
            ActiveLinkColor = Color.FromArgb(22, 101, 216),
            VisitedLinkColor = Color.FromArgb(22, 101, 216),
            Font = new Font(Font.FontFamily, 8.5F),
            Margin = new Padding(0)
        };

        link.LinkClicked += (_, _) => OpenExternalUrl(url);

        return link;
    }

    private static void ConfigurePrimaryButton(Button button, string text, EventHandler handler)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Height = 42;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(22, 101, 216);
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
        button.Margin = text.EndsWith("video", StringComparison.OrdinalIgnoreCase)
            ? new Padding(0, 0, 6, 0)
            : new Padding(6, 0, 0, 0);
        button.Click += handler;
    }

    private static void ConfigureSecondaryButton(Button button, string text, EventHandler handler)
    {
        button.Text = text;
        button.Width = 112;
        button.Height = 38;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.White;
        button.ForeColor = Color.FromArgb(31, 42, 55);
        button.FlatAppearance.BorderColor = Color.FromArgb(198, 207, 218);
        button.Margin = new Padding(0, 0, 8, 0);
        button.Click += handler;
    }

    private void SetReadyState()
    {
        if (_ytDlpPath is null)
        {
            _videoButton.Enabled = false;
            _audioButton.Enabled = false;
            SetStatus("yt-dlp.exe was not found", 0);
            return;
        }

        SetStatus("Choose video or audio", 0);
    }

    private async Task StartDownloadAsync(DownloadKind kind)
    {
        if (_downloadProcess is not null || _ytDlpPath is null)
        {
            return;
        }

        Directory.CreateDirectory(_downloadDirectory);
        Program.Log($"Starting {kind.ToString().ToLowerInvariant()} download from {_source}: {_url}");

        ProcessStartInfo startInfo = new()
        {
            FileName = _ytDlpPath,
            WorkingDirectory = _downloadDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        AddCommonArguments(startInfo);

        if (kind == DownloadKind.Video)
        {
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("bv*+ba/b");
            startInfo.ArgumentList.Add("--merge-output-format");
            startInfo.ArgumentList.Add("mp4");
        }
        else
        {
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("bestaudio/best");
            startInfo.ArgumentList.Add("-x");
            startInfo.ArgumentList.Add("--audio-format");
            startInfo.ArgumentList.Add("mp3");
            startInfo.ArgumentList.Add("--audio-quality");
            startInfo.ArgumentList.Add("0");
        }

        startInfo.ArgumentList.Add(_url);

        using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        _downloadProcess = process;

        process.OutputDataReceived += (_, e) => HandleYtDlpLine(e.Data);
        process.ErrorDataReceived += (_, e) => HandleYtDlpLine(e.Data);

        SetBusyState(kind);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                SetStatus("Done - saved in Downloads\\DLP", 100);
                Program.Log($"{kind} download completed.");
            }
            else
            {
                SetStatus("Download failed - check app.log", 0);
                Program.Log($"{kind} download failed with exit code {process.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Could not start download - check app.log", 0);
            Program.Log($"Download start failed: {ex}");
        }
        finally
        {
            _downloadProcess = null;
            SetIdleButtons();
        }
    }

    private void AddCommonArguments(ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Add("--newline");
        startInfo.ArgumentList.Add("--no-playlist");
        startInfo.ArgumentList.Add("--no-mtime");
        startInfo.ArgumentList.Add("--windows-filenames");
        startInfo.ArgumentList.Add("-P");
        startInfo.ArgumentList.Add(_downloadDirectory);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("%(title).200B [%(id)s].%(ext)s");

        if (_ffmpegPath is not null)
        {
            startInfo.ArgumentList.Add("--ffmpeg-location");
            startInfo.ArgumentList.Add(Path.GetDirectoryName(_ffmpegPath) ?? _ffmpegPath);
        }
    }

    private void HandleYtDlpLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Program.Log($"yt-dlp: {line}");

        Match match = ProgressRegex.Match(line);

        if (!match.Success || !double.TryParse(match.Groups["percent"].Value, out double percent))
        {
            return;
        }

        int value = Math.Clamp((int)Math.Round(percent), 0, 100);
        SetStatus($"Downloading {value}%", value);
    }

    private void SetBusyState(DownloadKind kind)
    {
        _videoButton.Enabled = false;
        _audioButton.Enabled = false;
        _progressBar.Visible = true;
        _progressBar.Value = 0;
        SetStatus(kind == DownloadKind.Video ? "Downloading best video" : "Downloading best audio", 0);
    }

    private void SetIdleButtons()
    {
        bool hasYtDlp = _ytDlpPath is not null;
        _videoButton.Enabled = hasYtDlp;
        _audioButton.Enabled = hasYtDlp;
    }

    private void SetStatus(string text, int progress)
    {
        if (IsDisposed)
        {
            return;
        }

        void Apply()
        {
            _statusLabel.Text = text;
            if (progress > 0)
            {
                _progressBar.Visible = true;
            }

            _progressBar.Value = Math.Clamp(progress, _progressBar.Minimum, _progressBar.Maximum);
        }

        if (InvokeRequired)
        {
            BeginInvoke(Apply);
            return;
        }

        Apply();
    }

    private void CancelDownload()
    {
        Process? process = _downloadProcess;

        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            SetStatus("Canceled", 0);
            Program.Log("Download canceled by user.");
        }
        catch (Exception ex)
        {
            Program.Log($"Cancel failed: {ex}");
        }
    }

    private void OpenDownloadFolder()
    {
        Directory.CreateDirectory(_downloadDirectory);

        ProcessStartInfo startInfo = new()
        {
            FileName = _downloadDirectory,
            UseShellExecute = true
        };

        Process.Start(startInfo);
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Program.Log($"Open link failed: {ex}");
        }
    }

    private static string Shorten(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxLength - 1), "...");
    }

    private enum DownloadKind
    {
        Video,
        Audio
    }
}

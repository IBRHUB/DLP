using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Krypton.Toolkit;

internal sealed partial class DownloadForm : Form
{
    private static readonly Regex ProgressRegex = new(@"(?<percent>\d{1,3}(?:\.\d+)?)%", RegexOptions.Compiled);

    private string _url;
    private string? _audioUrl;
    private string? _fallbackUrl;
    private string _source;
    private string? _title;
    private readonly string? _referer;
    private readonly string? _userAgent;
    private readonly string? _initialCookieBrowser;
    private string _downloadDirectory;
    private readonly string? _ytDlpPath;
    private readonly string? _ffmpegPath;

    private readonly Label _statusLabel = new();
    private readonly Panel _statusIndicator = new();
    private readonly TableLayoutPanel _progressPanel = new();
    private readonly DlpProgressBar _progressBar = new();
    private readonly Label _progressValueLabel = new();
    private readonly KryptonTextBox _urlBox = new();
    private readonly KryptonTextBox _savePathBox = new();
    private readonly KryptonButton _linkButton = new();
    private readonly KryptonButton _previewButton = new();
    private readonly KryptonCheckButton _videoButton = new();
    private readonly KryptonCheckButton _audioButton = new();
    private readonly KryptonComboBox _qualitySelect = new();
    private readonly KryptonComboBox _formatSelect = new();
    private readonly KryptonButton _browseButton = new();
    private readonly KryptonButton _downloadButton = new();
    private readonly KryptonButton _settingsButton = new();
    private readonly KryptonButton _openFolderButton = new();
    private readonly KryptonButton _updateButton = new();
    private readonly KryptonButton _cancelButton = new();
    private readonly KryptonButton _openLogButton = new();
    private readonly KryptonToggleSwitch _embedSubsSwitch = new();
    private readonly KryptonToggleSwitch _cookiesSwitch = new();
    private readonly KryptonComboBox _browserSelect = new();
    private readonly NotifyIcon _notifyIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolTip _toolTips = new();
    private Label _detectedKindLabel = new();
    private Label _detectedTitleLabel = new();
    private Label _detectedDurationLabel = new();

    private Process? _downloadProcess;
    private bool _isPreparingDownload;
    private bool _isUpdatingApp;
    private bool _isUpdatingYtDlp;
    private bool _isProbing;
    private bool _hasShownTrayNotice;

    private enum StatusTone
    {
        Idle,
        Busy,
        Success,
        Warning,
        Error
    }

    private enum DownloadKind
    {
        Video,
        Audio
    }

    private sealed record QualityChoice(string Label, int? Height)
    {
        public override string ToString() => Label;
    }

    public DownloadForm(
        string url,
        string? audioUrl,
        string? fallbackUrl,
        string source,
        string? title,
        string? referer,
        string? userAgent,
        string? cookieBrowser)
    {
        _url = url;
        _audioUrl = audioUrl;
        _fallbackUrl = fallbackUrl;
        _source = source;
        _title = title;
        _referer = referer;
        _userAgent = userAgent;
        _initialCookieBrowser = cookieBrowser;
        _downloadDirectory = Program.GetDownloadDirectory();
        _ytDlpPath = ToolResolver.ResolveToolPath("DLP_YTDLP_PATH", "yt-dlp.exe");
        _ffmpegPath = ToolResolver.ResolveToolPath("DLP_FFMPEG_PATH", "ffmpeg.exe");

        BuildUi();
        ConfigureBrowserSelect();
        PopulateQualityOptions([]);
        PopulateFormatOptions(DownloadKind.Video);
        ApplyInitialCookieBrowser();
        SetInitialSourceState();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _browserSelect.DroppedDown = false;
        _notifyIcon.Visible = false;
        CancelDownload();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _notifyIcon.Dispose();
        _trayMenu.Dispose();
        _toolTips.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (WindowState == FormWindowState.Minimized)
        {
            BeginInvoke(new Action(() => HideToTray(showNotice: true)));
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        TopMost = true;
        BringToFront();
        Activate();

        BeginInvoke(new Action(async () =>
        {
            TopMost = false;

            if (HasUsableSource())
            {
                _downloadButton.Focus();
                await ProbeSourceAsync();
            }
            else
            {
                _urlBox.Focus();
            }
        }));
    }

    private void SetInitialSourceState()
    {
        _urlBox.Text = _url;
        _savePathBox.Text = ShortDisplayPath(_downloadDirectory);
        SetDetectedMetadata(
            !string.IsNullOrWhiteSpace(_audioUrl) ? "Video" : "Video",
            _title,
            null);

        if (_ytDlpPath is null)
        {
            _downloadButton.Enabled = false;
            SetStatus("yt-dlp.exe was not found", 0);
            return;
        }

        if (HasUsableSource())
        {
            SetStatus("Ready to read link details", 0);
        }
        else
        {
            SetStatus("Paste a link to begin", 0);
        }

        SetIdleButtons();
    }

    private async Task StartDownloadAsync()
    {
        if (_downloadProcess is not null || _ytDlpPath is null || _isPreparingDownload || _isUpdatingApp || _isProbing)
        {
            return;
        }

        if (!TryApplySourceFromInput(out string downloadUrl))
        {
            SetStatus("Paste a valid HTTPS link", 0);
            return;
        }

        DownloadKind kind = _audioButton.Checked ? DownloadKind.Audio : DownloadKind.Video;
        string downloadDirectory = ResolveSelectedDownloadDirectory();
        CryptAccessScope? folderAccess = null;

        try
        {
            folderAccess = BeginDownloadFolderAccess(downloadDirectory, out downloadDirectory);
            Directory.CreateDirectory(downloadDirectory);
            Program.Log($"Starting {kind.ToString().ToLowerInvariant()} download from {_source}: {downloadUrl}");
            SetPreparingDownloadState();

            bool createDuplicateCopy = false;

            if (TitleDuplicateDetector.TryFindExistingDownload(downloadDirectory, _title, out string? existingFilePath)
                && existingFilePath is not null
                && !ConfirmDuplicateDownload(existingFilePath))
            {
                SetStatus("Already downloaded", 0);
                Program.Log($"Download skipped existing title '{_title}': {existingFilePath}");
                _isPreparingDownload = false;
                SetIdleButtons();
                return;
            }

            createDuplicateCopy = existingFilePath is not null;

            YtDlpDownloadOptions options = GetYtDlpOptions() with { SaveDirectory = downloadDirectory };

            if (kind == DownloadKind.Video && !string.IsNullOrWhiteSpace(_audioUrl))
            {
                await StartPairedVideoDownloadAsync(downloadDirectory, createDuplicateCopy, options);
                return;
            }

            string effectiveDownloadUrl = kind == DownloadKind.Audio && !string.IsNullOrWhiteSpace(_audioUrl)
                ? _audioUrl
                : downloadUrl;

            if (await TryInstagramCapturedFallbackFirstAsync(kind, effectiveDownloadUrl, downloadDirectory, createDuplicateCopy))
            {
                return;
            }

            if (await TryBuiltInDirectFirstAsync(kind, effectiveDownloadUrl, downloadDirectory, createDuplicateCopy))
            {
                return;
            }

            if (options.UseCookies)
            {
                Program.Log($"Using browser cookies for yt-dlp: {options.Browser}");
            }

            IReadOnlyList<YtDlpDownloadAttempt> attempts = YtDlpSites.GetDownloadAttempts(
                effectiveDownloadUrl,
                _referer,
                options.UseCookies ? options.Browser : null);
            SetBusyState(kind);

            try
            {
                _isPreparingDownload = false;

                for (int attemptIndex = 0; attemptIndex < attempts.Count; attemptIndex++)
                {
                    YtDlpDownloadAttempt attempt = attempts[attemptIndex];
                    YtDlpDownloadOptions attemptOptions = attempt.SuppressCookies
                        ? options with { UseCookies = false }
                        : options;
                    ProcessStartInfo startInfo = new()
                    {
                        FileName = _ytDlpPath,
                        WorkingDirectory = downloadDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };

                    AddCommonArguments(startInfo, downloadDirectory, createDuplicateCopy, attempt);

                    if (kind == DownloadKind.Video)
                    {
                        YtDlpArgumentBuilder.AddVideoArguments(startInfo, attemptOptions, effectiveDownloadUrl, attempt);
                    }
                    else
                    {
                        YtDlpArgumentBuilder.AddAudioArguments(startInfo, attemptOptions, effectiveDownloadUrl);
                    }

                    attempt.AddTo(startInfo);
                    startInfo.ArgumentList.Add(effectiveDownloadUrl);

                    using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
                    YtDlpRunDiagnostics diagnostics = new(
                        effectiveDownloadUrl,
                        _referer,
                        _userAgent,
                        attemptOptions.UseCookies ? attemptOptions.Browser : null,
                        kind == DownloadKind.Video ? _fallbackUrl : null);
                    SetDownloadProcess(process);

                    Program.Log($"yt-dlp attempt {attemptIndex + 1}/{attempts.Count}: {attempt.Name}");
                    diagnostics.LogStart(_ytDlpPath, Program.Log, startInfo);
                    process.OutputDataReceived += (_, e) => HandleYtDlpLine(e.Data);
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
                            SetStatus("Done - saved", 100);
                            Program.Log($"{kind} download completed.");
                            return;
                        }

                        SetDownloadProcess(null);
                        diagnostics.LogFailure(process.ExitCode, Program.Log);

                        if (attemptIndex < attempts.Count - 1)
                        {
                            SetStatus($"Retrying {attempts[attemptIndex + 1].Name}", 0);
                            Program.Log($"Retrying yt-dlp with attempt: {attempts[attemptIndex + 1].Name}");
                            continue;
                        }

                        if (await TryBuiltInFallbackAsync(
                            kind,
                            GetBuiltInFallbackUrl(kind, effectiveDownloadUrl),
                            GetBuiltInFallbackAudioUrl(kind, null),
                            downloadDirectory,
                            createDuplicateCopy))
                        {
                            return;
                        }

                        SetStatus("Download failed - check DLP.log", 0);
                        Program.Log($"{kind} download failed with exit code {process.ExitCode}.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        SetDownloadProcess(null);

                        if (attemptIndex < attempts.Count - 1)
                        {
                            SetStatus($"Retrying {attempts[attemptIndex + 1].Name}", 0);
                            Program.Log($"Download attempt failed: {ex.Message}");
                            Program.Log($"Retrying yt-dlp with attempt: {attempts[attemptIndex + 1].Name}");
                            continue;
                        }

                        if (await TryBuiltInFallbackAsync(
                            kind,
                            GetBuiltInFallbackUrl(kind, effectiveDownloadUrl),
                            GetBuiltInFallbackAudioUrl(kind, null),
                            downloadDirectory,
                            createDuplicateCopy))
                        {
                            return;
                        }

                        SetStatus("Could not start download - check DLP.log", 0);
                        Program.Log($"Download start failed: {ex}");
                        return;
                    }
                }
            }
            finally
            {
                SetDownloadProcess(null);
                _isPreparingDownload = false;
                SetIdleButtons();
            }
        }
        finally
        {
            folderAccess?.Dispose();
        }
    }

    private async Task<bool> TryBuiltInDirectFirstAsync(
        DownloadKind kind,
        string downloadUrl,
        string downloadDirectory,
        bool createDuplicateCopy)
    {
        if (YtDlpPlatformPolicy.ShouldPreferYtDlp(downloadUrl, _referer)
            && !Instagram.IsDirectMediaUrl(downloadUrl))
        {
            Program.Log($"Skipping built-in direct first for yt-dlp platform URL: {downloadUrl}");
            return false;
        }

        if (!BuiltInMediaDownloader.CanDownload(downloadUrl, null))
        {
            return false;
        }

        _isPreparingDownload = false;
        SetBusyState(kind);
        SetStatus("Downloading with DLP", 0);
        Program.Log($"Starting built-in media download: url={downloadUrl}");

        try
        {
            bool success = await BuiltInMediaDownloader.DownloadAsync(
                downloadUrl,
                null,
                downloadDirectory,
                _title,
                _referer,
                _userAgent,
                _ffmpegPath,
                createDuplicateCopy,
                Program.Log,
                SetStatus,
                SetDownloadProcess);

            if (success)
            {
                SetStatus("Done - saved", 100);
                return true;
            }
        }
        catch (Exception ex)
        {
            Program.Log($"Built-in media download failed before yt-dlp: {ex.Message}");
        }
        finally
        {
            SetDownloadProcess(null);
        }

        SetStatus("Trying yt-dlp", 0);
        return false;
    }

    private async Task<bool> TryInstagramCapturedFallbackFirstAsync(
        DownloadKind kind,
        string downloadUrl,
        string downloadDirectory,
        bool createDuplicateCopy)
    {
        if (kind != DownloadKind.Video
            || string.IsNullOrWhiteSpace(_fallbackUrl)
            || !string.Equals(YtDlpSites.DetectPlatform(downloadUrl, _referer), Instagram.DisplayName, StringComparison.Ordinal)
            || !BuiltInMediaDownloader.CanDownload(_fallbackUrl, null))
        {
            return false;
        }

        _isPreparingDownload = false;
        SetBusyState(kind);
        SetStatus("Trying Instagram direct media", 0);
        Program.Log($"Trying captured Instagram media before yt-dlp: {_fallbackUrl}");

        try
        {
            bool success = await BuiltInMediaDownloader.DownloadAsync(
                _fallbackUrl,
                null,
                downloadDirectory,
                _title,
                _referer,
                _userAgent,
                _ffmpegPath,
                createDuplicateCopy,
                Program.Log,
                SetStatus,
                SetDownloadProcess);

            if (success)
            {
                SetStatus("Done - saved", 100);
                return true;
            }

            Program.Log("Captured Instagram media failed before yt-dlp; continuing with yt-dlp attempts.");
        }
        catch (Exception ex)
        {
            Program.Log($"Captured Instagram media failed before yt-dlp: {ex.Message}");
        }
        finally
        {
            SetDownloadProcess(null);
        }

        SetStatus("Trying yt-dlp", 0);
        return false;
    }

    private async Task StartPairedVideoDownloadAsync(
        string downloadDirectory,
        bool createDuplicateCopy,
        YtDlpDownloadOptions options)
    {
        if (_ytDlpPath is null || string.IsNullOrWhiteSpace(_audioUrl))
        {
            _isPreparingDownload = false;
            SetIdleButtons();
            return;
        }

        if (_ffmpegPath is null)
        {
            SetStatus("ffmpeg.exe was not found", 0);
            Program.Log("Paired media download failed: ffmpeg.exe was not found.");
            _isPreparingDownload = false;
            SetIdleButtons();
            return;
        }

        SetBusyState(DownloadKind.Video);

        try
        {
            _isPreparingDownload = false;
            Program.Log($"Starting paired media download from {_source}: video={_url} audio={_audioUrl}");

            int exitCode = await DirectMediaPairDownloader.DownloadAndMergeAsync(
                _url,
                _audioUrl,
                downloadDirectory,
                _title,
                _ytDlpPath,
                _ffmpegPath,
                _referer,
                _userAgent,
                options.UseCookies ? options.Browser : null,
                createDuplicateCopy,
                Program.Log,
                SetStatus,
                SetDownloadProcess);

            if (exitCode == 0)
            {
                SetStatus("Done - saved", 100);
            }
            else if (await TryBuiltInFallbackAsync(
                DownloadKind.Video,
                GetBuiltInFallbackUrl(DownloadKind.Video, _url),
                GetBuiltInFallbackAudioUrl(DownloadKind.Video, _audioUrl),
                downloadDirectory,
                createDuplicateCopy))
            {
                return;
            }
            else
            {
                SetStatus("Download failed - check DLP.log", 0);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Could not start download - check DLP.log", 0);
            Program.Log($"Paired media download failed: {ex}");
        }
        finally
        {
            SetDownloadProcess(null);
            _isPreparingDownload = false;
            SetIdleButtons();
        }
    }

    private async Task<bool> TryBuiltInFallbackAsync(
        DownloadKind kind,
        string downloadUrl,
        string? audioUrl,
        string downloadDirectory,
        bool createDuplicateCopy)
    {
        if (!BuiltInMediaDownloader.CanDownload(downloadUrl, audioUrl))
        {
            Program.Log($"Built-in media fallback unavailable for URL: {downloadUrl} audioPair={audioUrl is not null}");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(audioUrl) && _ffmpegPath is null)
        {
            Program.Log("Built-in paired media fallback skipped: ffmpeg.exe was not found.");
            return false;
        }

        if (!ConfirmBuiltInFallback(!string.IsNullOrWhiteSpace(audioUrl)))
        {
            Program.Log("Built-in media fallback declined by user.");
            return false;
        }

        SetBusyState(kind);
        SetStatus("Trying DLP direct download", 0);
        Program.Log($"Starting built-in media fallback: url={downloadUrl} audioPair={audioUrl is not null}");

        try
        {
            bool success = await BuiltInMediaDownloader.DownloadAsync(
                downloadUrl,
                audioUrl,
                downloadDirectory,
                _title,
                _referer,
                _userAgent,
                _ffmpegPath,
                createDuplicateCopy,
                Program.Log,
                SetStatus,
                SetDownloadProcess);

            SetStatus(success ? "Done - saved" : "DLP direct download failed", success ? 100 : 0);
            return true;
        }
        catch (Exception ex)
        {
            Program.Log($"Built-in media fallback failed: {ex}");
            SetStatus("DLP direct download failed", 0);
            return true;
        }
        finally
        {
            SetDownloadProcess(null);
        }
    }

    private string GetBuiltInFallbackUrl(DownloadKind kind, string primaryUrl)
    {
        return kind == DownloadKind.Video && !string.IsNullOrWhiteSpace(_fallbackUrl)
            ? _fallbackUrl
            : primaryUrl;
    }

    private string? GetBuiltInFallbackAudioUrl(DownloadKind kind, string? primaryAudioUrl)
    {
        return kind == DownloadKind.Video && !string.IsNullOrWhiteSpace(_fallbackUrl)
            ? null
            : primaryAudioUrl;
    }

    private bool ConfirmBuiltInFallback(bool audioPair)
    {
        string mode = audioPair ? "captured video and audio links" : "the captured media link";
        DialogResult result = MessageBox.Show(
            this,
            $"yt-dlp could not download this media.{Environment.NewLine}{Environment.NewLine}Try DLP direct download using {mode}?",
            "DLP direct download",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        return result == DialogResult.Yes;
    }

    private bool ConfirmDuplicateDownload(string existingFilePath)
    {
        string fileName = Path.GetFileName(existingFilePath);
        DialogResult result = MessageBox.Show(
            this,
            $"This video looks already downloaded.{Environment.NewLine}{Environment.NewLine}{fileName}{Environment.NewLine}{Environment.NewLine}Continue anyway?",
            "DLP",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (result == DialogResult.Yes)
        {
            Program.Log($"Duplicate title accepted by user '{_title}': {existingFilePath}");
            return true;
        }

        return false;
    }

    private void AddCommonArguments(
        ProcessStartInfo startInfo,
        string downloadDirectory,
        bool createDuplicateCopy,
        YtDlpDownloadAttempt? attempt)
    {
        startInfo.ArgumentList.Add("--newline");
        if (attempt?.AllowPlaylist != true)
        {
            startInfo.ArgumentList.Add("--no-playlist");
        }
        startInfo.ArgumentList.Add("--no-mtime");
        startInfo.ArgumentList.Add("--windows-filenames");
        YtDlpNetworkArgumentBuilder.AddNetworkArguments(startInfo, _referer, _userAgent);
        startInfo.ArgumentList.Add("-P");
        startInfo.ArgumentList.Add(downloadDirectory);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(createDuplicateCopy
            ? $"%(title).200B [%(id)s] copy-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.%(ext)s"
            : "%(title).200B [%(id)s].%(ext)s");

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

        Program.Log($"yt-dlp: {YtDlpRunDiagnostics.CleanLine(line)}");

        Match match = ProgressRegex.Match(line);

        if (!match.Success || !double.TryParse(match.Groups["percent"].Value, out double percent))
        {
            return;
        }

        int value = Math.Clamp((int)Math.Round(percent), 0, 100);
        SetStatus($"Downloading {value}%", value);
    }

    private async Task ProbeSourceAsync()
    {
        if (_ytDlpPath is null)
        {
            SetStatus("yt-dlp.exe was not found", 0);
            return;
        }

        if (!TryApplySourceFromInput(out string sourceUrl))
        {
            SetStatus("Paste a valid HTTPS link", 0);
            return;
        }

        if (_isProbing || _downloadProcess is not null)
        {
            return;
        }

        _isProbing = true;
        SetOptionControlsEnabled(false);
        SetStatus("Fetching link details", 0);

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(35));
            MediaProbeResult result = await MediaProbeService.ProbeAsync(
                _ytDlpPath,
                sourceUrl,
                _referer,
                _userAgent,
                _cookiesSwitch.Checked ? CookieBrowserCatalog.Normalize(_browserSelect.SelectedItem?.ToString()) : null,
                timeout.Token);

            _title = result.Title;
            SetDetectedMetadata(result.MediaType, result.Title, result.Duration);
            PopulateQualityOptions(result.VideoHeights);
            SetStatus("Ready to download", 0);
        }
        catch (Exception ex)
        {
            Program.Log($"Link details failed: {ex.Message}");
            SetDetectedMetadata("Link", _title ?? "Details unavailable", null);
            PopulateQualityOptions([]);
            SetStatus("Details unavailable", 0);
        }
        finally
        {
            _isProbing = false;
            SetIdleButtons();
        }
    }

    private void SelectDownloadKind(DownloadKind kind)
    {
        bool video = kind == DownloadKind.Video;
        _videoButton.Checked = video;
        _audioButton.Checked = !video;
        PopulateFormatOptions(kind);
        _qualitySelect.Enabled = video && _downloadButton.Enabled;
        SetStatus(video ? "Video selected" : "Audio selected", 0);
    }

    private void PopulateQualityOptions(IReadOnlyList<int> heights)
    {
        _qualitySelect.BeginUpdate();
        _qualitySelect.Items.Clear();

        if (heights.Count > 0)
        {
            for (int index = 0; index < heights.Count; index++)
            {
                int height = heights[index];
                string label = index == 0 ? $"{height}p (Best)" : $"{height}p";
                _qualitySelect.Items.Add(new QualityChoice(label, height));
            }
        }

        _qualitySelect.Items.Add(new QualityChoice("Best available", null));
        _qualitySelect.SelectedIndex = 0;
        _qualitySelect.EndUpdate();
    }

    private void PopulateFormatOptions(DownloadKind kind)
    {
        string selected = GetSelectedFormat();
        _formatSelect.BeginUpdate();
        _formatSelect.Items.Clear();

        string[] formats = kind == DownloadKind.Video
            ? ["MP4", "MKV", "WEBM"]
            : ["MP3", "M4A", "OPUS", "WAV"];

        foreach (string format in formats)
        {
            _formatSelect.Items.Add(format);
        }

        int selectedIndex = Array.FindIndex(formats, format => string.Equals(format, selected, StringComparison.OrdinalIgnoreCase));
        _formatSelect.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        _formatSelect.EndUpdate();
    }

    private int? GetSelectedQualityHeight()
    {
        return _qualitySelect.SelectedItem is QualityChoice choice ? choice.Height : null;
    }

    private string GetSelectedFormat()
    {
        return _formatSelect.SelectedItem?.ToString() ?? (_audioButton.Checked ? "MP3" : "MP4");
    }

    private void SetDetectedMetadata(string mediaType, string? title, TimeSpan? duration)
    {
        _detectedKindLabel.Text = $"Detected: {mediaType}";
        _detectedTitleLabel.Text = $"Title: {Shorten(string.IsNullOrWhiteSpace(title) ? "Waiting for link" : title, 44)}";
        _detectedDurationLabel.Text = $"Duration: {FormatDuration(duration)}";
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration is null)
        {
            return "--:--";
        }

        return duration.Value.TotalHours >= 1
            ? duration.Value.ToString(@"hh\:mm\:ss")
            : duration.Value.ToString(@"mm\:ss");
    }

    private void SetBusyState(DownloadKind kind)
    {
        _downloadButton.Enabled = false;
        _updateButton.Enabled = false;
        _settingsButton.Enabled = false;
        SetOptionControlsEnabled(false);
        UpdateProgress(0);
        SetStatus(kind == DownloadKind.Video ? "Downloading video" : "Downloading audio", 0);
    }

    private void SetPreparingDownloadState()
    {
        _isPreparingDownload = true;
        _downloadButton.Enabled = false;
        _updateButton.Enabled = false;
        _settingsButton.Enabled = false;
        SetOptionControlsEnabled(false);
        UpdateProgress(0);
        SetStatus("Preparing download", 0);
    }

    private void SetIdleButtons()
    {
        bool canRunAppTools = !_isUpdatingApp
            && !_isUpdatingYtDlp
            && !_isPreparingDownload
            && !_isProbing
            && _downloadProcess is null;
        bool hasSource = HasUsableSource();
        bool canRunYtDlp = _ytDlpPath is not null && canRunAppTools && hasSource;

        _downloadButton.Enabled = canRunYtDlp;
        _previewButton.Enabled = canRunAppTools && hasSource;
        _updateButton.Enabled = canRunAppTools;
        _settingsButton.Enabled = canRunAppTools;
        SetOptionControlsEnabled(canRunAppTools);
        _downloadButton.Enabled = canRunYtDlp;
        UpdateStatusActions(ResolveStatusTone(_statusLabel.Text, _progressBar.Value));
    }

    private async Task UpdateAllAsync()
    {
        if (_downloadProcess is not null || _isPreparingDownload || _isUpdatingApp || _isUpdatingYtDlp)
        {
            return;
        }

        await UpdateAppAsync();

        if (!IsDisposed && !_isUpdatingApp)
        {
            await UpdateYtDlpAsync();
        }
    }

    private async Task UpdateAppAsync()
    {
        if (_downloadProcess is not null || _isPreparingDownload || _isUpdatingApp || _isUpdatingYtDlp)
        {
            return;
        }

        bool installerStarted = false;
        _isUpdatingApp = true;
        _downloadButton.Enabled = false;
        _updateButton.Enabled = false;
        _settingsButton.Enabled = false;
        SetOptionControlsEnabled(false);
        UpdateProgress(0);
        SetStatus("Checking app update", 0);

        try
        {
            Program.Log("Checking app update");
            AppUpdateInfo updateInfo = await AppUpdater.CheckAsync();

            if (updateInfo.Status == AppUpdateStatus.UpToDate)
            {
                SetStatus("DLP is up to date", 0);
                Program.Log($"App update skipped current={updateInfo.CurrentVersion} latest={updateInfo.LatestVersion}");
                return;
            }

            if (updateInfo.Status != AppUpdateStatus.Available)
            {
                SetStatus(updateInfo.Message ?? "App update unavailable", 0);
                Program.Log($"App update unavailable: {updateInfo.Message ?? updateInfo.Status.ToString()}");
                return;
            }

            SetStatus($"Downloading DLP {updateInfo.LatestVersion}", 0);

            string installerPath = await AppUpdater.DownloadInstallerAsync(
                updateInfo,
                progress => SetStatus($"Downloading update {progress}%", progress));

            SetStatus("Installing update", 100);
            Program.Log($"Starting app update installer: {installerPath}");
            AppUpdater.StartInstaller(installerPath);
            installerStarted = true;

            BeginInvoke(new Action(Application.Exit));
        }
        catch (Exception ex)
        {
            SetStatus("App update failed check DLP.log", 0);
            Program.Log($"App update failed: {ex}");
        }
        finally
        {
            if (!installerStarted)
            {
                _isUpdatingApp = false;
                SetIdleButtons();
            }
        }
    }

    private async Task UpdateYtDlpAsync()
    {
        if (_ytDlpPath is null || _downloadProcess is not null || _isUpdatingYtDlp || _isUpdatingApp)
        {
            return;
        }

        _isUpdatingYtDlp = true;
        _downloadButton.Enabled = false;
        _updateButton.Enabled = false;
        _settingsButton.Enabled = false;
        SetOptionControlsEnabled(false);
        SetStatus("Updating yt-dlp", 0);

        ProcessStartInfo startInfo = new()
        {
            FileName = _ytDlpPath,
            WorkingDirectory = Path.GetDirectoryName(_ytDlpPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("--update");

        using Process process = new() { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) => LogYtDlpUpdateLine(e.Data);
        process.ErrorDataReceived += (_, e) => LogYtDlpUpdateLine(e.Data);

        try
        {
            Program.Log($"Starting yt-dlp update: {_ytDlpPath}");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                SetStatus("yt-dlp updated", 0);
                Program.Log("yt-dlp update completed");
            }
            else
            {
                SetStatus("yt-dlp update failed check DLP.log", 0);
                Program.Log($"yt-dlp update failed with exit code {process.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Could not update yt-dlp check DLP.log", 0);
            Program.Log($"yt-dlp update start failed: {ex}");
        }
        finally
        {
            _isUpdatingYtDlp = false;
            SetIdleButtons();
        }
    }

    private static void LogYtDlpUpdateLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            Program.Log($"yt-dlp update: {line}");
        }
    }

    private void SetStatus(string text, int progress)
    {
        if (IsDisposed)
        {
            return;
        }

        void Apply()
        {
            StatusTone tone = ResolveStatusTone(text, progress);
            _statusLabel.Text = FormatStatusText(text);
            ApplyStatusTone(tone);
            UpdateProgress(progress);
        }

        if (InvokeRequired)
        {
            BeginInvoke(Apply);
            return;
        }

        Apply();
    }

    private void ApplyStatusTone(StatusTone tone)
    {
        Color color = tone switch
        {
            StatusTone.Busy => DlpTheme.AccentInteractive,
            StatusTone.Success => DlpTheme.Success,
            StatusTone.Warning => DlpTheme.Warning,
            StatusTone.Error => DlpTheme.Destructive,
            _ => DlpTheme.TextSecondary
        };

        _statusLabel.ForeColor = color;
        UpdateStatusActions(tone);
    }

    private void SetDownloadProcess(Process? process)
    {
        _downloadProcess = process;

        if (IsDisposed)
        {
            return;
        }

        void Apply() => UpdateStatusActions(ResolveStatusTone(_statusLabel.Text, _progressBar.Value));

        if (InvokeRequired)
        {
            BeginInvoke(Apply);
            return;
        }

        Apply();
    }

    private void UpdateStatusActions(StatusTone tone)
    {
        bool canCancelDownload = tone == StatusTone.Busy && _downloadProcess is not null;

        _cancelButton.Visible = canCancelDownload;
        _cancelButton.Enabled = canCancelDownload;
        _openLogButton.Visible = tone == StatusTone.Error;
    }

    private void UpdateProgress(int progress)
    {
        int value = Math.Clamp(progress, 0, 100);

        _progressBar.Value = value;
        _progressValueLabel.Text = $"{value}%";
    }

    private static StatusTone ResolveStatusTone(string text, int progress)
    {
        string normalized = text.ToLowerInvariant();

        if (normalized.Contains("failed", StringComparison.Ordinal)
            || normalized.Contains("not found", StringComparison.Ordinal)
            || normalized.Contains("could not", StringComparison.Ordinal))
        {
            return StatusTone.Error;
        }

        if (normalized.Contains("done", StringComparison.Ordinal)
            || normalized.Contains("updated", StringComparison.Ordinal)
            || normalized.Contains("up to date", StringComparison.Ordinal))
        {
            return StatusTone.Success;
        }

        if (normalized.Contains("already downloaded", StringComparison.Ordinal)
            || normalized.Contains("canceled", StringComparison.Ordinal)
            || normalized.Contains("unavailable", StringComparison.Ordinal)
            || normalized.Contains("details unavailable", StringComparison.Ordinal))
        {
            return StatusTone.Warning;
        }

        if (progress > 0
            || normalized.Contains("downloading", StringComparison.Ordinal)
            || normalized.Contains("preparing", StringComparison.Ordinal)
            || normalized.Contains("checking", StringComparison.Ordinal)
            || normalized.Contains("installing", StringComparison.Ordinal)
            || normalized.Contains("updating", StringComparison.Ordinal)
            || normalized.Contains("retrying", StringComparison.Ordinal)
            || normalized.Contains("trying", StringComparison.Ordinal)
            || normalized.Contains("fetching", StringComparison.Ordinal))
        {
            return StatusTone.Busy;
        }

        return StatusTone.Idle;
    }

    private static string FormatStatusText(string text)
    {
        return text
            .Replace(" - check DLP.log", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" check DLP.log", string.Empty, StringComparison.OrdinalIgnoreCase);
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
            Program.Log("Download canceled by user");
        }
        catch (Exception ex)
        {
            Program.Log($"Cancel failed: {ex}");
        }
    }

    private void OpenDownloadFolder()
    {
        string selectedDirectory = ResolveSelectedDownloadDirectory();

        try
        {
            string directory = selectedDirectory;

            if (IsDefaultDownloadDirectory(selectedDirectory))
            {
                CryptStatus access = Crypt.UnlockForCurrentUser();
                directory = access.Directory;
            }
            else
            {
                Directory.CreateDirectory(directory);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus("Could not open folder", 0);
            Program.Log($"Open folder failed: {ex}");
        }
    }

    private void BrowseSaveDirectory()
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Choose where DLP saves downloads",
            SelectedPath = Directory.Exists(_downloadDirectory)
                ? _downloadDirectory
                : Program.GetDownloadDirectory(),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        _downloadDirectory = Path.GetFullPath(dialog.SelectedPath);
        _savePathBox.Text = ShortDisplayPath(_downloadDirectory);
        SetStatus("Save folder selected", 0);
    }

    private void PreviewSource()
    {
        if (!TryApplySourceFromInput(out string sourceUrl))
        {
            SetStatus("Paste a valid HTTPS link", 0);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = sourceUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus("Could not open preview", 0);
            Program.Log($"Preview open failed: {ex}");
        }
    }

    private static void OpenLogFile()
    {
        try
        {
            string logPath = DlpLogger.LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? AppContext.BaseDirectory);

            if (!File.Exists(logPath))
            {
                File.WriteAllText(logPath, string.Empty, Encoding.UTF8);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Program.Log($"Open log failed: {ex}");
        }
    }

    private void OnSourceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        e.SuppressKeyPress = true;
        _ = ProbeSourceAsync();
    }

    private void OnSourceDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.Text) == true)
        {
            e.Effect = DragDropEffects.Copy;
            return;
        }

        e.Effect = DragDropEffects.None;
    }

    private void OnSourceDragDrop(object? sender, DragEventArgs e)
    {
        string? text = e.Data?.GetData(DataFormats.Text) as string;

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _urlBox.Text = text.Trim();
        _ = ProbeSourceAsync();
    }

    private bool TryApplySourceFromInput(out string sourceUrl)
    {
        sourceUrl = string.Empty;
        string candidate = _urlBox.Text.Trim();

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        sourceUrl = uri.AbsoluteUri;
        _url = sourceUrl;
        _source = string.IsNullOrWhiteSpace(_source) ? "manual" : _source;
        _urlBox.Text = sourceUrl;
        return true;
    }

    private bool HasUsableSource()
    {
        string candidate = _urlBox.Text.Trim();
        return Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveSelectedDownloadDirectory()
    {
        return string.IsNullOrWhiteSpace(_downloadDirectory)
            ? Program.GetDownloadDirectory()
            : Path.GetFullPath(_downloadDirectory);
    }

    private CryptAccessScope? BeginDownloadFolderAccess(string requestedDirectory, out string activeDirectory)
    {
        if (!IsDefaultDownloadDirectory(requestedDirectory))
        {
            activeDirectory = requestedDirectory;
            return null;
        }

        CryptAccessScope scope = Crypt.BeginOperationAccess(
            "manual-download",
            CryptAccessMode.Modify);
        activeDirectory = scope.DirectoryPath;
        return scope;
    }

    private static bool IsDefaultDownloadDirectory(string directory)
    {
        static string Normalize(string value)
        {
            return Path.GetFullPath(value)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return string.Equals(
            Normalize(directory),
            Normalize(Program.GetDownloadDirectory()),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortDisplayPath(string path)
    {
        string defaultDirectory = Program.GetDownloadDirectory();

        if (IsDefaultDownloadDirectory(path))
        {
            return "Downloads\\DLP";
        }

        string fullPath = Path.GetFullPath(path);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(userProfile)
            && fullPath.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return "~" + fullPath[userProfile.Length..];
        }

        return fullPath;
    }

    private static string Shorten(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxLength - 1), "...");
    }
}

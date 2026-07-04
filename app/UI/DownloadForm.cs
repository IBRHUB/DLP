using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Krypton.Toolkit;

internal sealed partial class DownloadForm : KryptonForm
{
    private static readonly Regex ProgressRegex = new(@"(?<percent>\d{1,3}(?:\.\d+)?)%", RegexOptions.Compiled);

    private readonly string _url;
    private readonly string? _audioUrl;
    private readonly string? _fallbackUrl;
    private readonly string _source;
    private readonly string? _title;
    private readonly string? _referer;
    private readonly string? _userAgent;
    private readonly string? _initialCookieBrowser;
    private readonly string _downloadDirectory;
    private readonly string? _ytDlpPath;
    private readonly string? _ffmpegPath;

    private readonly Label _statusLabel = new();
    private readonly Panel _statusIndicator = new();
    private readonly ProgressBar _progressBar = new();
    private readonly KryptonButton _videoButton = new();
    private readonly KryptonButton _audioButton = new();
    private readonly KryptonButton _openFolderButton = new();
    private readonly KryptonButton _updateButton = new();
    private readonly KryptonButton _openLogButton = new();
    private readonly KryptonToggleSwitch _embedSubsSwitch = new();
    private readonly KryptonToggleSwitch _cookiesSwitch = new();
    private readonly KryptonComboBox _browserSelect = new();
    private readonly NotifyIcon _notifyIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private TableLayoutPanel _browserRow = new();
    private Label _browserSettingLabel = new();

    private Process? _downloadProcess;
    private bool _isPreparingDownload;
    private bool _isUpdatingApp;
    private bool _isUpdatingYtDlp;
    private bool _hasShownTrayNotice;

    private enum StatusTone
    {
        Idle,
        Busy,
        Success,
        Warning,
        Error
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
        ApplyInitialCookieBrowser();
        SetReadyState();
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

        BeginInvoke(new Action(() => TopMost = false));
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
        if (_downloadProcess is not null || _ytDlpPath is null || _isPreparingDownload || _isUpdatingApp)
        {
            return;
        }

        using CryptAccessScope folderAccess = Crypt.BeginOperationAccess(
            "manual-download",
            CryptAccessMode.Modify);
        Directory.CreateDirectory(_downloadDirectory);
        Program.Log($"Starting {kind.ToString().ToLowerInvariant()} download from {_source}: {_url}");
        SetPreparingDownloadState();

        bool createDuplicateCopy = false;

        if (TitleDuplicateDetector.TryFindExistingDownload(_downloadDirectory, _title, out string? existingFilePath)
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

        YtDlpDownloadOptions options = GetYtDlpOptions();

        if (kind == DownloadKind.Video && !string.IsNullOrWhiteSpace(_audioUrl))
        {
            await StartPairedVideoDownloadAsync(createDuplicateCopy, options);
            return;
        }

        string downloadUrl = kind == DownloadKind.Audio && !string.IsNullOrWhiteSpace(_audioUrl)
            ? _audioUrl
            : _url;

        if (await TryInstagramCapturedFallbackFirstAsync(kind, downloadUrl, createDuplicateCopy))
        {
            return;
        }

        if (await TryBuiltInDirectFirstAsync(kind, downloadUrl, createDuplicateCopy))
        {
            return;
        }

        if (options.UseCookies)
        {
            Program.Log($"Using browser cookies for yt-dlp: {options.Browser}");
        }

        IReadOnlyList<YtDlpDownloadAttempt> attempts = YtDlpSites.GetDownloadAttempts(
            downloadUrl,
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
                    WorkingDirectory = _downloadDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                AddCommonArguments(startInfo, createDuplicateCopy, attempt);

                if (kind == DownloadKind.Video)
                {
                    YtDlpArgumentBuilder.AddVideoArguments(startInfo, attemptOptions, downloadUrl, attempt);
                }
                else
                {
                    YtDlpArgumentBuilder.AddAudioArguments(startInfo, attemptOptions, downloadUrl);
                }

                attempt.AddTo(startInfo);
                startInfo.ArgumentList.Add(downloadUrl);

                using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
                YtDlpRunDiagnostics diagnostics = new(
                    downloadUrl,
                    _referer,
                    _userAgent,
                    attemptOptions.UseCookies ? attemptOptions.Browser : null,
                    kind == DownloadKind.Video ? _fallbackUrl : null);
                _downloadProcess = process;

                Program.Log($"yt-dlp attempt {attemptIndex + 1}/{attempts.Count}: {attempt.Name}");
                diagnostics.LogStart(_ytDlpPath, Program.Log, startInfo);
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
                        SetStatus("Done - saved in Downloads\\DLP", 100);
                        Program.Log($"{kind} download completed.");
                        return;
                    }

                    _downloadProcess = null;
                    diagnostics.LogFailure(process.ExitCode, Program.Log);

                    if (attemptIndex < attempts.Count - 1)
                    {
                        SetStatus($"Retrying {attempts[attemptIndex + 1].Name}", 0);
                        Program.Log($"Retrying yt-dlp with attempt: {attempts[attemptIndex + 1].Name}");
                        continue;
                    }

                    if (await TryBuiltInFallbackAsync(
                        kind,
                        GetBuiltInFallbackUrl(kind, downloadUrl),
                        GetBuiltInFallbackAudioUrl(kind, null),
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
                    _downloadProcess = null;

                    if (attemptIndex < attempts.Count - 1)
                    {
                        SetStatus($"Retrying {attempts[attemptIndex + 1].Name}", 0);
                        Program.Log($"Download attempt failed: {ex.Message}");
                        Program.Log($"Retrying yt-dlp with attempt: {attempts[attemptIndex + 1].Name}");
                        continue;
                    }

                    if (await TryBuiltInFallbackAsync(
                        kind,
                        GetBuiltInFallbackUrl(kind, downloadUrl),
                        GetBuiltInFallbackAudioUrl(kind, null),
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
            _downloadProcess = null;
            _isPreparingDownload = false;
            SetIdleButtons();
        }
    }

    private async Task<bool> TryBuiltInDirectFirstAsync(
        DownloadKind kind,
        string downloadUrl,
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
                _downloadDirectory,
                _title,
                _referer,
                _userAgent,
                _ffmpegPath,
                createDuplicateCopy,
                Program.Log,
                SetStatus,
                process => _downloadProcess = process);

            if (success)
            {
                SetStatus("Done - saved in Downloads\\DLP", 100);
                return true;
            }
        }
        catch (Exception ex)
        {
            Program.Log($"Built-in media download failed before yt-dlp: {ex.Message}");
        }
        finally
        {
            _downloadProcess = null;
        }

        SetStatus("Trying yt-dlp", 0);
        return false;
    }

    private async Task<bool> TryInstagramCapturedFallbackFirstAsync(
        DownloadKind kind,
        string downloadUrl,
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
                _downloadDirectory,
                _title,
                _referer,
                _userAgent,
                _ffmpegPath,
                createDuplicateCopy,
                Program.Log,
                SetStatus,
                process => _downloadProcess = process);

            if (success)
            {
                SetStatus("Done - saved in Downloads\\DLP", 100);
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
            _downloadProcess = null;
        }

        SetStatus("Trying yt-dlp", 0);
        return false;
    }

    private async Task StartPairedVideoDownloadAsync(bool createDuplicateCopy, YtDlpDownloadOptions options)
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
                _downloadDirectory,
                _title,
                _ytDlpPath,
                _ffmpegPath,
                _referer,
                _userAgent,
                options.UseCookies ? options.Browser : null,
                createDuplicateCopy,
                Program.Log,
                SetStatus,
                process => _downloadProcess = process);

            if (exitCode == 0)
            {
                SetStatus("Done - saved in Downloads\\DLP", 100);
            }
            else if (await TryBuiltInFallbackAsync(
                DownloadKind.Video,
                GetBuiltInFallbackUrl(DownloadKind.Video, _url),
                GetBuiltInFallbackAudioUrl(DownloadKind.Video, _audioUrl),
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
            _downloadProcess = null;
            _isPreparingDownload = false;
            SetIdleButtons();
        }
    }

    private async Task<bool> TryBuiltInFallbackAsync(
        DownloadKind kind,
        string downloadUrl,
        string? audioUrl,
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
                _downloadDirectory,
                _title,
                _referer,
                _userAgent,
                _ffmpegPath,
                createDuplicateCopy,
                Program.Log,
                SetStatus,
                process => _downloadProcess = process);

            SetStatus(success ? "Done - saved in Downloads\\DLP" : "DLP direct download failed", success ? 100 : 0);
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
            _downloadProcess = null;
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
        startInfo.ArgumentList.Add(_downloadDirectory);
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

    private void SetBusyState(DownloadKind kind)
    {
        _videoButton.Enabled = false;
        _audioButton.Enabled = false;
        _updateButton.Enabled = false;
        SetOptionControlsEnabled(false);
        _progressBar.Visible = true;
        _progressBar.Value = 0;
        SetStatus(kind == DownloadKind.Video ? "Downloading best video" : "Downloading best audio", 0);
    }

    private void SetPreparingDownloadState()
    {
        _isPreparingDownload = true;
        _videoButton.Enabled = false;
        _audioButton.Enabled = false;
        _updateButton.Enabled = false;
        SetOptionControlsEnabled(false);
        _progressBar.Visible = false;
        _progressBar.Value = 0;
        SetStatus("Preparing download", 0);
    }

    private void SetIdleButtons()
    {
        bool canRunAppTools = !_isUpdatingApp
            && !_isUpdatingYtDlp
            && !_isPreparingDownload
            && _downloadProcess is null;
        bool canRunYtDlp = _ytDlpPath is not null && canRunAppTools;

        _videoButton.Enabled = canRunYtDlp;
        _audioButton.Enabled = canRunYtDlp;
        _updateButton.Enabled = canRunAppTools;
        SetOptionControlsEnabled(canRunYtDlp);
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
        _videoButton.Enabled = false;
        _audioButton.Enabled = false;
        _updateButton.Enabled = false;
        SetOptionControlsEnabled(false);
        _progressBar.Visible = false;
        _progressBar.Value = 0;
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
            _progressBar.Visible = true;

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
            _progressBar.Visible = false;
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
        _videoButton.Enabled = false;
        _audioButton.Enabled = false;
        _updateButton.Enabled = false;
        SetOptionControlsEnabled(false);
        _progressBar.Visible = false;
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

    private void ApplyStatusTone(StatusTone tone)
    {
        Color color = tone switch
        {
            StatusTone.Busy => DlpTheme.Accent,
            StatusTone.Success => DlpTheme.Success,
            StatusTone.Warning => DlpTheme.Warning,
            StatusTone.Error => DlpTheme.Destructive,
            _ => DlpTheme.TextSecondary
        };

        _statusIndicator.BackColor = color;
        _statusLabel.ForeColor = color;
        _progressBar.ForeColor = tone == StatusTone.Error ? DlpTheme.Destructive : DlpTheme.AccentActive;
        _openLogButton.Visible = tone == StatusTone.Error;
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
            || normalized.Contains("unavailable", StringComparison.Ordinal))
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
            || normalized.Contains("trying", StringComparison.Ordinal))
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
        CryptStatus access = Crypt.UnlockForCurrentUser();

        ProcessStartInfo startInfo = new()
        {
            FileName = access.Directory,
            UseShellExecute = true
        };

        Process.Start(startInfo);
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

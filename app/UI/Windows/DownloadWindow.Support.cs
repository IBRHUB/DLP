using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

internal sealed partial class DownloadWindow
{
    private async void ReadLinkDetails_Click(object sender, RoutedEventArgs e)
    {
        await ProbeSourceAsync();
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        PreviewSource();
    }

    private void Video_Click(object sender, RoutedEventArgs e)
    {
        SelectDownloadKind(DownloadKind.Video);
    }

    private void Audio_Click(object sender, RoutedEventArgs e)
    {
        SelectDownloadKind(DownloadKind.Audio);
    }

    private void CookiesSwitch_Changed(object sender, RoutedEventArgs e)
    {
        UpdateBrowserComboEnabled();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        BrowseSaveDirectory();
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        await StartDownloadAsync();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await UpdateAllAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelDownload();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        OpenLogFile();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenDownloadFolder();
    }

    private YtDlpDownloadOptions GetYtDlpOptions()
    {
        return new YtDlpDownloadOptions(
            _embedSubsSwitch.IsChecked == true,
            _cookiesSwitch.IsChecked == true,
            CookieBrowserCatalog.Normalize(_browserSelect.SelectedItem?.ToString()) ?? "brave",
            GetSelectedQualityHeight(),
            GetSelectedFormat(),
            ResolveSelectedDownloadDirectory());
    }

    private void UpdateBrowserComboEnabled()
    {
        _browserSelect.IsEnabled = _cookiesSwitch.IsEnabled && _cookiesSwitch.IsChecked == true;
    }

    private void SetOptionControlsEnabled(bool enabled)
    {
        _embedSubsSwitch.IsEnabled = enabled;
        _cookiesSwitch.IsEnabled = enabled;
        _qualitySelect.IsEnabled = enabled && _videoButton.IsChecked == true;
        _formatSelect.IsEnabled = enabled;
        _browseButton.IsEnabled = enabled;
        _urlBox.IsEnabled = enabled;
        _linkButton.IsEnabled = enabled;
        _previewButton.IsEnabled = enabled && HasUsableSource();
        _videoButton.IsEnabled = enabled;
        _audioButton.IsEnabled = enabled;
        UpdateBrowserComboEnabled();
    }

    private void ConfigureBrowserSelect()
    {
        _browserSelect.Items.Clear();

        foreach (string browser in CookieBrowserCatalog.Values)
        {
            _browserSelect.Items.Add(FormatBrowserName(browser));
        }

        _browserSelect.SelectedIndex = 0;
        _browserSelect.Width = 132;
        _browserSelect.Height = 32;
        _browserSelect.Visibility = Visibility.Collapsed;
    }

    private void ApplyInitialCookieBrowser()
    {
        if (string.IsNullOrWhiteSpace(_initialCookieBrowser))
        {
            return;
        }

        string displayName = FormatBrowserName(_initialCookieBrowser);
        int index = _browserSelect.Items.IndexOf(displayName);

        if (index >= 0)
        {
            _browserSelect.SelectedIndex = index;
        }

        _cookiesSwitch.IsChecked = true;
        UpdateBrowserComboEnabled();
    }

    private void ConfigureTrayIcon()
    {
        Forms.ToolStripMenuItem showItem = new("Show DLP", null, (_, _) => Dispatcher.BeginInvoke(new Action(RestoreFromTray)));
        Forms.ToolStripMenuItem openFolderItem = new("Open folder", null, (_, _) => Dispatcher.BeginInvoke(new Action(OpenDownloadFolder)));
        Forms.ToolStripMenuItem exitItem = new("Exit", null, (_, _) => Dispatcher.BeginInvoke(new Action(Close)));

        _trayMenu.Items.AddRange(
        [
            showItem,
            openFolderItem,
            new Forms.ToolStripSeparator(),
            exitItem
        ]);

        _notifyIcon.Text = "DLP";
        _notifyIcon.Icon = GetTrayIcon();
        _notifyIcon.ContextMenuStrip = _trayMenu;
        _notifyIcon.Visible = false;
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));
    }

    private void HideToTray(bool showNotice)
    {
        if (_isClosed)
        {
            return;
        }

        _browserSelect.IsDropDownOpen = false;
        _notifyIcon.Visible = true;
        ShowInTaskbar = false;
        Hide();

        if (showNotice && !_hasShownTrayNotice)
        {
            _notifyIcon.BalloonTipTitle = "DLP";
            _notifyIcon.BalloonTipText = "DLP is still running. Double-click the tray icon to restore it.";
            _notifyIcon.ShowBalloonTip(2500);
            _hasShownTrayNotice = true;
        }
    }

    private void RestoreFromTray()
    {
        if (_isClosed)
        {
            return;
        }

        _notifyIcon.Visible = false;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private static Drawing.Icon GetTrayIcon()
    {
        try
        {
            string? executablePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;

            return !string.IsNullOrWhiteSpace(executablePath)
                ? Drawing.Icon.ExtractAssociatedIcon(executablePath) ?? Drawing.SystemIcons.Application
                : Drawing.SystemIcons.Application;
        }
        catch
        {
            return Drawing.SystemIcons.Application;
        }
    }

    private static string FormatBrowserName(string browser) => CookieBrowserCatalog.ToDisplayName(browser);

    private static string GetCurrentVersionText()
    {
        Assembly assembly = typeof(Program).Assembly;
        string version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        int metadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        if (metadataIndex >= 0)
        {
            version = version[..metadataIndex];
        }

        return version;
    }
}

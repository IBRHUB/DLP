using System.Drawing;
using System.Windows.Forms;

internal sealed partial class DownloadForm
{
    private void ConfigureTrayIcon()
    {
        ToolStripMenuItem showItem = new("Show DLP", null, (_, _) => RestoreFromTray());
        ToolStripMenuItem openFolderItem = new("Open folder", null, (_, _) => OpenDownloadFolder());
        ToolStripMenuItem exitItem = new("Exit", null, (_, _) => Close());

        _trayMenu.Items.AddRange(new ToolStripItem[]
        {
            showItem,
            openFolderItem,
            new ToolStripSeparator(),
            exitItem
        });

        _notifyIcon.Text = "DLP";
        _notifyIcon.Icon = GetTrayIcon();
        _notifyIcon.ContextMenuStrip = _trayMenu;
        _notifyIcon.Visible = false;
        _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void HideToTray(bool showNotice)
    {
        if (IsDisposed)
        {
            return;
        }

        _browserSelect.DroppedDown = false;
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
        if (IsDisposed)
        {
            return;
        }

        _notifyIcon.Visible = false;
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private static Icon GetTrayIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }
}

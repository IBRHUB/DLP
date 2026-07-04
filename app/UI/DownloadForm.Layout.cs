using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Krypton.Toolkit;

internal sealed partial class DownloadForm
{
    private void BuildUi()
    {
        Text = "DLP";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = true;
        Width = 560;
        Height = 600;
        MinimumSize = new Size(560, 600);
        PaletteMode = PaletteMode.MaterialDark;
        ConfigureFormChrome();
        BackColor = DlpTheme.Bg;
        Font = new Font("Segoe UI", 9.5F);

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            BackColor = DlpTheme.Bg,
            Padding = new Padding(14),
            RowCount = 1,
            ColumnCount = 1
        };

        TableLayoutPanel frame = new()
        {
            Dock = DockStyle.Fill,
            BackColor = DlpTheme.Border,
            Padding = new Padding(1),
            RowCount = 1,
            ColumnCount = 1,
            Margin = new Padding(0)
        };

        TableLayoutPanel surface = new()
        {
            Dock = DockStyle.Fill,
            BackColor = DlpTheme.Bg,
            RowCount = 3,
            ColumnCount = 1,
            Margin = new Padding(0)
        };

        surface.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        surface.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        surface.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        surface.Controls.Add(BuildHeaderPanel(), 0, 0);
        surface.Controls.Add(CreateDivider(), 0, 1);
        surface.Controls.Add(BuildMainContent(), 0, 2);

        frame.Controls.Add(surface, 0, 0);
        root.Controls.Add(frame, 0, 0);
        Controls.Add(root);
        ConfigureTrayIcon();
    }

    private TableLayoutPanel BuildHeaderPanel()
    {
        TableLayoutPanel header = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = DlpTheme.Bg,
            Padding = new Padding(20, 16, 20, 16),
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0)
        };

        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Panel mark = new()
        {
            Width = 38,
            Height = 38,
            BackColor = DlpTheme.TextPrimary,
            Margin = new Padding(0, 0, 12, 0),
            Anchor = AnchorStyles.Left
        };

        TableLayoutPanel copy = new()
        {
            AutoSize = true,
            BackColor = DlpTheme.Bg,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Left
        };

        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label title = new()
        {
            Text = "DLP",
            AutoSize = true,
            ForeColor = DlpTheme.TextPrimary,
            Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 1)
        };

        Label subtitle = new()
        {
            Text = "Download bridge",
            AutoSize = true,
            ForeColor = DlpTheme.TextSecondary,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Regular),
            Margin = new Padding(0)
        };

        copy.Controls.Add(title, 0, 0);
        copy.Controls.Add(subtitle, 0, 1);

        LinkLabel sourceLink = CreateHeaderLink("IBRHUB/DLP", "https://github.com/IBRHUB/DLP");
        KryptonButton minimizeButton = new();
        KryptonButton closeButton = new();
        ConfigureChromeButton(minimizeButton, "_", (_, _) => WindowState = FormWindowState.Minimized);
        ConfigureChromeButton(closeButton, "X", (_, _) => Close(), destructive: true);

        TableLayoutPanel headerActions = new()
        {
            AutoSize = true,
            BackColor = DlpTheme.Bg,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Right
        };

        headerActions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerActions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerActions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerActions.Controls.Add(sourceLink, 0, 0);
        headerActions.Controls.Add(minimizeButton, 1, 0);
        headerActions.Controls.Add(closeButton, 2, 0);

        EnableWindowDrag(header);
        EnableWindowDrag(mark);
        EnableWindowDrag(copy);
        EnableWindowDrag(title);
        EnableWindowDrag(subtitle);

        header.Controls.Add(mark, 0, 0);
        header.Controls.Add(copy, 1, 0);
        header.Controls.Add(headerActions, 2, 0);

        return header;
    }

    private TableLayoutPanel BuildMainContent()
    {
        TableLayoutPanel content = new()
        {
            Dock = DockStyle.Fill,
            BackColor = DlpTheme.Bg,
            Padding = new Padding(20, 18, 20, 14),
            RowCount = 8,
            ColumnCount = 1,
            Margin = new Padding(0)
        };

        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        TableLayoutPanel urlPanel = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = DlpTheme.Bg,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 20)
        };

        urlPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        urlPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label urlCaption = CreateSectionCaption("Source");

        KryptonTextBox urlBox = new()
        {
            Text = _url,
            ReadOnly = true,
            PaletteMode = PaletteMode.Custom,
            Dock = DockStyle.Top,
            Height = 34,
            Margin = new Padding(0)
        };
        ConfigureReadOnlyTextBox(urlBox);

        urlPanel.Controls.Add(urlCaption);
        urlPanel.Controls.Add(urlBox);

        TableLayoutPanel optionsPanel = BuildOptionsPanel();

        Label downloadCaption = CreateSectionCaption("Download");

        TableLayoutPanel actions = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = DlpTheme.Bg,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 18)
        };

        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        ConfigurePrimaryButton(_videoButton, "Video", async (_, _) => await StartDownloadAsync(DownloadKind.Video));
        ConfigureSecondaryButton(_audioButton, "Audio", async (_, _) => await StartDownloadAsync(DownloadKind.Audio));
        _videoButton.Margin = new Padding(0, 0, 6, 0);
        _audioButton.Margin = new Padding(6, 0, 0, 0);

        actions.Controls.Add(_videoButton, 0, 0);
        actions.Controls.Add(_audioButton, 1, 0);

        TableLayoutPanel folderPanel = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = DlpTheme.Bg,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 18)
        };

        folderPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        folderPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        Label folderPath = new()
        {
            Text = "Saves to Downloads\\DLP",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = DlpTheme.TextMuted,
            Margin = new Padding(0, 0, 0, 8)
        };

        ConfigureSecondaryButton(_openFolderButton, "Open folder", (_, _) => OpenDownloadFolder());
        ConfigureSecondaryButton(_updateButton, "Update", async (_, _) => await UpdateAllAsync());
        _openFolderButton.Margin = new Padding(0, 0, 6, 0);
        _updateButton.Margin = new Padding(6, 0, 0, 0);

        folderPanel.Controls.Add(folderPath, 0, 0);
        folderPanel.SetColumnSpan(folderPath, 2);
        folderPanel.Controls.Add(_openFolderButton, 0, 1);
        folderPanel.Controls.Add(_updateButton, 1, 1);

        _progressBar.Dock = DockStyle.Top;
        _progressBar.Height = 10;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.BackColor = DlpTheme.Surface;
        _progressBar.ForeColor = DlpTheme.AccentActive;
        _progressBar.Margin = new Padding(0, 0, 0, 12);
        _progressBar.Visible = false;

        _statusLabel.Text = "Ready";
        _statusLabel.AutoSize = false;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.Height = 28;
        _statusLabel.ForeColor = DlpTheme.TextSecondary;

        content.Controls.Add(urlPanel, 0, 0);
        content.Controls.Add(optionsPanel, 0, 1);
        content.Controls.Add(downloadCaption, 0, 2);
        content.Controls.Add(actions, 0, 3);
        content.Controls.Add(folderPanel, 0, 4);
        content.Controls.Add(_progressBar, 0, 5);
        content.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = DlpTheme.Bg }, 0, 6);
        content.Controls.Add(BuildStatusPanel(), 0, 7);

        return content;
    }

    private static Panel CreateDivider() => new()
    {
        Dock = DockStyle.Fill,
        Height = 1,
        BackColor = DlpTheme.Border,
        Margin = new Padding(0)
    };

    private Label CreateSectionCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = DlpTheme.TextSecondary,
        Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 8)
    };

    private LinkLabel CreateHeaderLink(string text, string url)
    {
        LinkLabel link = new()
        {
            Text = text,
            AutoSize = true,
            BackColor = DlpTheme.SurfaceHover,
            LinkColor = DlpTheme.TextPrimary,
            ActiveLinkColor = DlpTheme.TextPrimary,
            VisitedLinkColor = DlpTheme.TextPrimary,
            LinkBehavior = LinkBehavior.NeverUnderline,
            Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold),
            Margin = new Padding(0),
            Padding = new Padding(10, 6, 10, 6),
            Anchor = AnchorStyles.Right
        };

        link.LinkClicked += (_, _) => OpenExternalUrl(url);

        return link;
    }

    private TableLayoutPanel BuildStatusPanel()
    {
        TableLayoutPanel statusPanel = new()
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            BackColor = DlpTheme.Bg,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 8, 0, 0),
            MinimumSize = new Size(0, 32)
        };

        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _statusIndicator.Width = 6;
        _statusIndicator.Height = 6;
        _statusIndicator.BackColor = DlpTheme.TextSecondary;
        _statusIndicator.Margin = new Padding(0, 10, 8, 0);
        _statusIndicator.Anchor = AnchorStyles.Left;

        ConfigureInlineButton(_openLogButton, "Log", (_, _) => OpenLogFile());
        _openLogButton.Visible = false;

        statusPanel.Controls.Add(_statusIndicator, 0, 0);
        statusPanel.Controls.Add(_statusLabel, 1, 0);
        statusPanel.Controls.Add(_openLogButton, 2, 0);

        return statusPanel;
    }

    private static void ConfigureChromeButton(
        KryptonButton button,
        string text,
        EventHandler handler,
        bool destructive = false)
    {
        button.Text = text;
        button.Width = 30;
        button.Height = 30;
        button.ButtonStyle = ButtonStyle.Custom1;
        button.PaletteMode = PaletteMode.Custom;
        button.Margin = new Padding(6, 0, 0, 0);
        button.TabStop = false;

        Color backColor = DlpTheme.SurfaceHover;
        Color hoverColor = destructive ? DlpTheme.Destructive : DlpTheme.Muted;
        Color textColor = DlpTheme.TextPrimary;
        Color hoverTextColor = destructive ? DlpTheme.AccentText : DlpTheme.TextPrimary;

        ApplyKryptonButtonState(button, backColor, backColor, textColor);
        ApplyKryptonButtonState(button.StateTracking, hoverColor, hoverColor, hoverTextColor);
        ApplyKryptonButtonState(button.StatePressed, hoverColor, hoverColor, hoverTextColor);
        ApplyKryptonButtonState(button.StateDisabled, DlpTheme.Bg, DlpTheme.Border, DlpTheme.DisabledText);
        button.Click += handler;
    }

    private static void ConfigureInlineButton(KryptonButton button, string text, EventHandler handler)
    {
        button.Text = text;
        button.Width = 54;
        button.Height = 28;
        button.ButtonStyle = ButtonStyle.Custom1;
        button.PaletteMode = PaletteMode.Custom;
        button.Margin = new Padding(10, 0, 0, 0);
        button.TabStop = false;
        ApplyKryptonButtonState(button, DlpTheme.SurfaceHover, DlpTheme.Border, DlpTheme.TextPrimary);
        ApplyKryptonButtonState(button.StateTracking, DlpTheme.Muted, DlpTheme.BorderStrong, DlpTheme.TextPrimary);
        ApplyKryptonButtonState(button.StatePressed, DlpTheme.Muted, DlpTheme.BorderStrong, DlpTheme.TextPrimary);
        ApplyKryptonButtonState(button.StateDisabled, DlpTheme.Bg, DlpTheme.Border, DlpTheme.DisabledText);
        button.Click += handler;
    }

    private void EnableWindowDrag(Control control)
    {
        control.MouseDown += BeginWindowDrag;
    }

    private void BeginWindowDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, WmNclButtonDown, HtCaption, 0);
    }

    private const int WmNclButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    private void ConfigureFormChrome()
    {
        if (StateCommon is not null)
        {
            ApplyKryptonFormState(StateCommon, DlpTheme.TextPrimary);
        }

        if (StateActive is not null)
        {
            ApplyKryptonFormState(StateActive, DlpTheme.TextPrimary);
        }

        if (StateInactive is not null)
        {
            ApplyKryptonFormState(StateInactive, DlpTheme.TextSecondary);
        }
    }

    private static void ApplyKryptonFormState(PaletteFormRedirect state, Color textColor)
    {
        state.Back.Color1 = DlpTheme.Bg;
        state.Back.Color2 = DlpTheme.Bg;
        ApplyKryptonFormBorder(state.Border);
        ApplyKryptonFormHeader(state.Header, textColor);
    }

    private static void ApplyKryptonFormState(PaletteForm state, Color textColor)
    {
        state.Back.Color1 = DlpTheme.Bg;
        state.Back.Color2 = DlpTheme.Bg;
        ApplyKryptonFormBorder(state.Border);
        ApplyKryptonFormHeader(state.Header, textColor);
    }

    private static void ApplyKryptonFormHeader(PaletteHeaderButtonRedirect header, Color textColor)
    {
        header.Back.Color1 = DlpTheme.Bg;
        header.Back.Color2 = DlpTheme.Bg;
        ApplyKryptonFormBorder(header.Border);
        header.Content.ShortText.Color1 = textColor;
        header.Content.ShortText.Color2 = textColor;
        header.Content.ShortText.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
    }

    private static void ApplyKryptonFormHeader(PaletteTripleMetric header, Color textColor)
    {
        header.Back.Color1 = DlpTheme.Bg;
        header.Back.Color2 = DlpTheme.Bg;
        ApplyKryptonFormBorder(header.Border);
        header.Content.ShortText.Color1 = textColor;
        header.Content.ShortText.Color2 = textColor;
        header.Content.ShortText.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
    }

    private static void ApplyKryptonFormBorder(PaletteFormBorder border)
    {
        border.Color1 = DlpTheme.Border;
        border.Color2 = DlpTheme.Border;
        border.DrawBorders = PaletteDrawBorders.All;
        border.Rounding = 0F;
        border.Width = 1;
    }

    private static void ApplyKryptonFormBorder(PaletteBorder border)
    {
        border.Color1 = DlpTheme.Border;
        border.Color2 = DlpTheme.Border;
        border.DrawBorders = PaletteDrawBorders.All;
        border.Rounding = 0F;
        border.Width = 1;
    }

    private TableLayoutPanel BuildOptionsPanel()
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = DlpTheme.Bg,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0, 0, 0, 14)
        };

        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label optionsCaption = CreateSectionCaption("Options");

        ConfigureToggleSwitch(_embedSubsSwitch);
        ConfigureToggleSwitch(_cookiesSwitch);
        _cookiesSwitch.CheckedChanged += (_, _) => UpdateBrowserComboEnabled();
        ConfigureBrowserSelect();

        TableLayoutPanel embedRow = CreateSettingRow("Embed subtitles", _embedSubsSwitch);
        TableLayoutPanel cookiesRow = CreateSettingRow("Browser cookies", _cookiesSwitch);
        _browserRow = CreateSettingRow("Browser", _browserSelect, out _browserSettingLabel);

        panel.Controls.Add(optionsCaption, 0, 0);
        panel.Controls.Add(embedRow, 0, 1);
        panel.Controls.Add(cookiesRow, 0, 2);
        panel.Controls.Add(_browserRow, 0, 3);

        UpdateBrowserComboEnabled();

        return panel;
    }

    private TableLayoutPanel CreateSettingRow(string title, Control control)
    {
        return CreateSettingRow(title, control, out _);
    }

    private TableLayoutPanel CreateSettingRow(string title, Control control, out Label titleLabel)
    {
        TableLayoutPanel row = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = DlpTheme.Bg,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 10),
            MinimumSize = new Size(0, 34)
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));

        titleLabel = new Label
        {
            Text = title,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = DlpTheme.TextPrimary,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Regular),
            Margin = new Padding(0, 7, 12, 0)
        };

        control.Dock = control switch
        {
            KryptonComboBox => DockStyle.Top,
            KryptonToggleSwitch => DockStyle.None,
            _ => DockStyle.Fill
        };
        control.Margin = new Padding(0, 1, 0, 1);
        control.Anchor = AnchorStyles.Right;

        row.Controls.Add(titleLabel, 0, 0);
        row.Controls.Add(control, 1, 0);

        return row;
    }

    private YtDlpDownloadOptions GetYtDlpOptions() => new(
        _embedSubsSwitch.Checked,
        _cookiesSwitch.Checked,
        CookieBrowserCatalog.Normalize(_browserSelect.SelectedItem?.ToString()) ?? "brave");

    private void UpdateBrowserComboEnabled()
    {
        bool showBrowser = _cookiesSwitch.Checked;
        bool allowBrowser = _cookiesSwitch.Enabled && showBrowser;

        if (!showBrowser)
        {
            _browserSelect.DroppedDown = false;
        }

        _browserRow.Visible = showBrowser;
        _browserSelect.Enabled = allowBrowser;
        _browserSettingLabel.ForeColor = allowBrowser ? DlpTheme.TextPrimary : DlpTheme.TextSecondary;
        _browserSelect.Invalidate();
    }

    private void SetOptionControlsEnabled(bool enabled)
    {
        _embedSubsSwitch.Enabled = enabled;
        _cookiesSwitch.Enabled = enabled;
        UpdateBrowserComboEnabled();
    }

    private void ConfigureBrowserSelect()
    {
        _browserSelect.BeginUpdate();
        _browserSelect.Items.Clear();

        foreach (string browser in CookieBrowserCatalog.Values)
        {
            _browserSelect.Items.Add(FormatBrowserName(browser));
        }

        _browserSelect.SelectedIndex = 0;
        _browserSelect.EndUpdate();

        _browserSelect.DropDownStyle = ComboBoxStyle.DropDownList;
        _browserSelect.PaletteMode = PaletteMode.Custom;
        _browserSelect.InputControlStyle = InputControlStyle.Custom1;
        _browserSelect.ItemStyle = ButtonStyle.Custom1;
        _browserSelect.DropButtonStyle = ButtonStyle.Custom1;
        _browserSelect.DropBackStyle = PaletteBackStyle.ControlCustom1;
        _browserSelect.BackColor = DlpTheme.Surface;
        _browserSelect.ForeColor = DlpTheme.TextPrimary;
        _browserSelect.Font = new Font(Font.FontFamily, 9.5F);
        _browserSelect.ItemHeight = 30;
        _browserSelect.IntegralHeight = false;
        _browserSelect.MaxDropDownItems = CookieBrowserCatalog.Values.Length;
        _browserSelect.DropDownHeight = (CookieBrowserCatalog.Values.Length * 30) + 2;
        _browserSelect.Enabled = false;
        ConfigureBrowserComboBox(_browserSelect);
        UpdateBrowserComboEnabled();
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

        _cookiesSwitch.Checked = true;
        UpdateBrowserComboEnabled();
    }

    private static string FormatBrowserName(string browser) => CookieBrowserCatalog.ToDisplayName(browser);

    private static void ConfigurePrimaryButton(KryptonButton button, string text, EventHandler handler)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Height = 42;
        button.ButtonStyle = ButtonStyle.Custom1;
        button.PaletteMode = PaletteMode.Custom;
        button.Margin = new Padding(0);
        ApplyKryptonButtonState(button, DlpTheme.AccentActive, DlpTheme.AccentActive, DlpTheme.AccentText);
        ApplyKryptonButtonState(button.StateTracking, DlpTheme.AccentHover, DlpTheme.AccentHover, DlpTheme.AccentText);
        ApplyKryptonButtonState(button.StatePressed, DlpTheme.Accent, DlpTheme.Accent, DlpTheme.AccentText);
        ApplyKryptonButtonState(button.StateDisabled, DlpTheme.Surface, DlpTheme.Border, DlpTheme.DisabledText);
        button.Click += handler;
    }

    private static void ConfigureSecondaryButton(KryptonButton button, string text, EventHandler handler)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Height = 40;
        button.ButtonStyle = ButtonStyle.Custom1;
        button.PaletteMode = PaletteMode.Custom;
        button.Margin = new Padding(0);
        ApplyKryptonButtonState(button, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary);
        ApplyKryptonButtonState(button.StateTracking, DlpTheme.SurfaceHover, DlpTheme.BorderStrong, DlpTheme.TextPrimary);
        ApplyKryptonButtonState(button.StatePressed, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary);
        ApplyKryptonButtonState(button.StateDisabled, DlpTheme.Bg, DlpTheme.Border, DlpTheme.DisabledText);
        button.Click += handler;
    }

    private static void ConfigureReadOnlyTextBox(KryptonTextBox textBox)
    {
        ApplyKryptonInputState(textBox.StateCommon, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary);
        ApplyKryptonInputState(textBox.StateNormal, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary);
        ApplyKryptonInputState(textBox.StateActive, DlpTheme.Surface, DlpTheme.AccentInteractive, DlpTheme.TextPrimary);
        ApplyKryptonInputState(textBox.StateDisabled, DlpTheme.Bg, DlpTheme.Border, DlpTheme.DisabledText);
    }

    private static void ConfigureBrowserComboBox(KryptonComboBox comboBox)
    {
        ApplyKryptonInputState(comboBox.StateCommon.ComboBox, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary);
        ApplyKryptonInputState(comboBox.StateNormal.ComboBox, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary);
        ApplyKryptonInputState(comboBox.StateActive.ComboBox, DlpTheme.Surface, DlpTheme.AccentInteractive, DlpTheme.TextPrimary);
        ApplyKryptonInputState(comboBox.StateDisabled.ComboBox, DlpTheme.Bg, DlpTheme.Border, DlpTheme.DisabledText);

        comboBox.StateCommon.DropBack.Color1 = DlpTheme.Surface;
        comboBox.StateCommon.DropBack.Color2 = DlpTheme.Surface;
        ApplyKryptonButtonState(comboBox.StateCommon.Item, DlpTheme.Surface, DlpTheme.Surface, DlpTheme.TextPrimary);
        ApplyKryptonButtonState(comboBox.StateNormal.Item, DlpTheme.Surface, DlpTheme.Surface, DlpTheme.TextPrimary);
        ApplyKryptonButtonState(comboBox.StateTracking.Item, DlpTheme.SurfaceHover, DlpTheme.SurfaceHover, DlpTheme.TextPrimary);
        ApplyKryptonButtonState(comboBox.StateDisabled.Item, DlpTheme.Bg, DlpTheme.Border, DlpTheme.DisabledText);
    }

    private static void ConfigureToggleSwitch(KryptonToggleSwitch toggle)
    {
        toggle.Size = new Size(48, 26);
        toggle.MinimumSize = new Size(48, 26);
        toggle.MaximumSize = new Size(48, 26);
        toggle.Cursor = Cursors.Hand;
        toggle.BackColor = DlpTheme.Bg;
        toggle.ForeColor = DlpTheme.TextPrimary;
        toggle.ToggleSwitchValues.UseThemeColors = false;
        toggle.ToggleSwitchValues.ShowText = false;
        toggle.ToggleSwitchValues.OnlyShowColorOnKnob = false;
        toggle.ToggleSwitchValues.EnableEmbossEffect = false;
        toggle.ToggleSwitchValues.AnimateGradientEffect = false;
        toggle.ToggleSwitchValues.EnableKnobGradient = false;
        toggle.ToggleSwitchValues.OnColor = DlpTheme.AccentActive;
        toggle.ToggleSwitchValues.OffColor = DlpTheme.BorderStrong;
        toggle.ToggleSwitchValues.CornerRadius = 12;

        ApplyKryptonToggleState(toggle.StateCommon, DlpTheme.BorderStrong, DlpTheme.BorderStrong, DlpTheme.TextPrimary);
        ApplyKryptonToggleState(toggle.StateNormal, DlpTheme.BorderStrong, DlpTheme.BorderStrong, DlpTheme.TextPrimary);
        ApplyKryptonToggleState(toggle.StateTracking, DlpTheme.SurfaceHover, DlpTheme.BorderStrong, DlpTheme.TextPrimary);
        ApplyKryptonToggleState(toggle.StatePressed, DlpTheme.AccentActive, DlpTheme.AccentActive, DlpTheme.AccentText);
        ApplyKryptonToggleState(toggle.StateDisabled, DlpTheme.Border, DlpTheme.Border, DlpTheme.DisabledText);
    }

    private static void ApplyKryptonInputState(
        PaletteInputControlTripleRedirect state,
        Color backColor,
        Color borderColor,
        Color textColor)
    {
        state.Back.Color1 = backColor;
        state.Border.Color1 = borderColor;
        state.Border.Color2 = borderColor;
        state.Border.DrawBorders = PaletteDrawBorders.All;
        state.Border.Rounding = 6F;
        state.Border.Width = 1;
        state.Content.Color1 = textColor;
        state.Content.Font = new Font("Segoe UI", 9.5F);
        state.Content.Padding = new Padding(8, 5, 8, 5);
    }

    private static void ApplyKryptonInputState(
        PaletteInputControlTripleStates state,
        Color backColor,
        Color borderColor,
        Color textColor)
    {
        state.Back.Color1 = backColor;
        state.Border.Color1 = borderColor;
        state.Border.Color2 = borderColor;
        state.Border.DrawBorders = PaletteDrawBorders.All;
        state.Border.Rounding = 6F;
        state.Border.Width = 1;
        state.Content.Color1 = textColor;
        state.Content.Font = new Font("Segoe UI", 9.5F);
        state.Content.Padding = new Padding(8, 5, 8, 5);
    }

    private static void ApplyKryptonButtonState(
        KryptonButton button,
        Color backColor,
        Color borderColor,
        Color textColor)
    {
        ApplyKryptonButtonState(button.StateCommon, backColor, borderColor, textColor);
        ApplyKryptonButtonState(button.StateNormal, backColor, borderColor, textColor);
        ApplyKryptonButtonState(button.OverrideFocus, backColor, DlpTheme.AccentInteractive, textColor);
    }

    private static void ApplyKryptonButtonState(
        PaletteTripleRedirect state,
        Color backColor,
        Color borderColor,
        Color textColor)
    {
        state.Back.Color1 = backColor;
        state.Back.Color2 = backColor;
        state.Border.Color1 = borderColor;
        state.Border.Color2 = borderColor;
        state.Border.DrawBorders = PaletteDrawBorders.All;
        state.Border.Rounding = 6F;
        state.Border.Width = 1;
        state.Content.ShortText.Color1 = textColor;
        state.Content.ShortText.Color2 = textColor;
        state.Content.ShortText.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        state.Content.Padding = new Padding(0);
    }

    private static void ApplyKryptonButtonState(
        PaletteTriple state,
        Color backColor,
        Color borderColor,
        Color textColor)
    {
        state.Back.Color1 = backColor;
        state.Back.Color2 = backColor;
        state.Border.Color1 = borderColor;
        state.Border.Color2 = borderColor;
        state.Border.DrawBorders = PaletteDrawBorders.All;
        state.Border.Rounding = 6F;
        state.Border.Width = 1;
        state.Content.ShortText.Color1 = textColor;
        state.Content.ShortText.Color2 = textColor;
        state.Content.ShortText.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        state.Content.Padding = new Padding(0);
    }

    private static void ApplyKryptonToggleState(
        PaletteTripleRedirect state,
        Color backColor,
        Color borderColor,
        Color textColor)
    {
        state.Back.Color1 = backColor;
        state.Back.Color2 = backColor;
        state.Border.Color1 = borderColor;
        state.Border.Color2 = borderColor;
        state.Border.DrawBorders = PaletteDrawBorders.All;
        state.Border.Rounding = 12F;
        state.Border.Width = 1;
        state.Content.ShortText.Color1 = textColor;
        state.Content.ShortText.Color2 = textColor;
    }

    private static void ApplyKryptonToggleState(
        PaletteTriple state,
        Color backColor,
        Color borderColor,
        Color textColor)
    {
        state.Back.Color1 = backColor;
        state.Back.Color2 = backColor;
        state.Border.Color1 = borderColor;
        state.Border.Color2 = borderColor;
        state.Border.DrawBorders = PaletteDrawBorders.All;
        state.Border.Rounding = 12F;
        state.Border.Width = 1;
        state.Content.ShortText.Color1 = textColor;
        state.Content.ShortText.Color2 = textColor;
    }
}

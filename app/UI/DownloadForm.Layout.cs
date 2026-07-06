using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

internal sealed partial class DownloadForm
{
    private static readonly Size MainClientSize = new(760, 760);
    private const int IconButtonSize = 40;
    private const string FontFamily = "Segoe UI";
    private const string IconFontFamily = "Segoe Fluent Icons";

    private void BuildUi()
    {
        Text = "DLP";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ControlBox = true;
        ShowIcon = false;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = MainClientSize;
        MinimumSize = Size;
        MaximumSize = Size;
        BackColor = DlpTheme.Bg;
        Font = new Font(FontFamily, 9.5F);
        AllowDrop = true;

        DragEnter += OnSourceDragEnter;
        DragDrop += OnSourceDragDrop;

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            BackColor = DlpTheme.Bg,
            Padding = new Padding(22, 16, 22, 14),
            RowCount = 10,
            ColumnCount = 1
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildSourceSection(), 0, 0);
        root.Controls.Add(BuildDetectedSection(), 0, 1);
        root.Controls.Add(BuildDownloadAsSection(), 0, 2);
        root.Controls.Add(BuildQualityFormatSection(), 0, 3);
        root.Controls.Add(BuildOptionsSection(), 0, 4);
        root.Controls.Add(BuildSaveSection(), 0, 5);
        root.Controls.Add(BuildPrimaryActionSection(), 0, 6);
        root.Controls.Add(BuildProgressSection(), 0, 7);
        root.Controls.Add(BuildFooterSection(), 0, 8);

        Controls.Add(root);
        ConfigureTrayIcon();
        ConfigureToolTips();
    }

    private Control BuildSourceSection()
    {
        TableLayoutPanel section = CreateSection("Source", "\uE71B", bottomMargin: 12);
        TableLayoutPanel row = new()
        {
            Dock = DockStyle.Top,
            Height = IconButtonSize,
            BackColor = DlpTheme.Bg,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, IconButtonSize));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, IconButtonSize));

        _urlBox.Dock = DockStyle.Fill;
        _urlBox.Height = IconButtonSize;
        _urlBox.Margin = new Padding(0, 0, 8, 0);
        _urlBox.PaletteMode = PaletteMode.Custom;
        _urlBox.AccessibleName = "Source URL";
        _urlBox.AllowDrop = true;
        _urlBox.Text = _url;
        _urlBox.CueHint.CueHintText = "Paste link here or drag & drop";
        _urlBox.CueHint.Color1 = DlpTheme.TextMuted;
        _urlBox.CueHint.Font = new Font(FontFamily, 9.5F, FontStyle.Regular);
        _urlBox.KeyDown += OnSourceKeyDown;
        _urlBox.DragEnter += OnSourceDragEnter;
        _urlBox.DragDrop += OnSourceDragDrop;
        ConfigureInput(_urlBox);

        ConfigureIconButton(_linkButton, "\uE71B", "Read link details", async (_, _) => await ProbeSourceAsync());
        _linkButton.Dock = DockStyle.Fill;
        _linkButton.Margin = Padding.Empty;

        row.Controls.Add(_urlBox, 0, 0);
        row.Controls.Add(_linkButton, 1, 0);
        section.Controls.Add(row, 0, 1);
        return section;
    }

    private Control BuildDetectedSection()
    {
        TableLayoutPanel row = CreateFramedRow(columnCount: 5, height: 48, bottomMargin: 14);
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));

        _detectedKindLabel = CreateMetaLabel("Detected: Video");
        _detectedTitleLabel = CreateMetaLabel("Title: Waiting for link");
        _detectedDurationLabel = CreateMetaLabel("Duration: --:--");

        ConfigureSecondaryButton(_previewButton, "Preview", (_, _) => PreviewSource());
        _previewButton.Dock = DockStyle.Fill;
        _previewButton.Margin = new Padding(0);

        row.Controls.Add(_detectedKindLabel, 0, 0);
        row.Controls.Add(_detectedTitleLabel, 1, 0);
        row.Controls.Add(_detectedDurationLabel, 2, 0);
        row.Controls.Add(_previewButton, 4, 0);
        return row;
    }

    private Control BuildDownloadAsSection()
    {
        TableLayoutPanel section = CreateSection("Download as", "\uE896", bottomMargin: 14);
        TableLayoutPanel row = new()
        {
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = DlpTheme.Bg,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0)
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        ConfigureModeButton(_videoButton, "Video", true);
        ConfigureModeButton(_audioButton, "Audio", false);
        _videoButton.Click += (_, _) => SelectDownloadKind(DownloadKind.Video);
        _audioButton.Click += (_, _) => SelectDownloadKind(DownloadKind.Audio);

        row.Controls.Add(_videoButton, 0, 0);
        row.Controls.Add(_audioButton, 2, 0);
        section.Controls.Add(row, 0, 1);
        return section;
    }

    private Control BuildQualityFormatSection()
    {
        TableLayoutPanel row = new()
        {
            Dock = DockStyle.Top,
            Height = 66,
            BackColor = DlpTheme.Bg,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 14)
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        row.Controls.Add(CreateFieldLabel("Quality"), 0, 0);
        row.Controls.Add(CreateFieldLabel("Format"), 1, 0);

        ConfigureComboBox(_qualitySelect);
        ConfigureComboBox(_formatSelect);

        Control qualityHost = WrapComboWithChevron(_qualitySelect);
        qualityHost.Margin = new Padding(0, 0, 14, 0);
        Control formatHost = WrapComboWithChevron(_formatSelect);
        formatHost.Margin = new Padding(0);

        row.Controls.Add(qualityHost, 0, 1);
        row.Controls.Add(formatHost, 1, 1);
        return row;
    }

    private Control BuildOptionsSection()
    {
        TableLayoutPanel section = CreateSection("Options", "\uE90F", bottomMargin: 14);
        TableLayoutPanel rows = new()
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = DlpTheme.Bg,
            RowCount = 2,
            ColumnCount = 1,
            Margin = new Padding(0)
        };

        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        ConfigureToggleSwitch(_embedSubsSwitch);
        ConfigureToggleSwitch(_cookiesSwitch);
        _cookiesSwitch.CheckedChanged += (_, _) => UpdateBrowserComboEnabled();

        rows.Controls.Add(CreateOptionRow("Embed subtitles (if available)", _embedSubsSwitch), 0, 0);
        rows.Controls.Add(CreateOptionRow("Include browser cookies", _cookiesSwitch), 0, 1);
        section.Controls.Add(rows, 0, 1);
        return section;
    }

    private Control BuildSaveSection()
    {
        TableLayoutPanel section = CreateSection("Save to", "\uE8B7", bottomMargin: 14);
        TableLayoutPanel row = new()
        {
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = DlpTheme.Bg,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));

        _savePathBox.Text = ShortDisplayPath(_downloadDirectory);
        _savePathBox.ReadOnly = true;
        _savePathBox.Dock = DockStyle.Fill;
        _savePathBox.Height = 40;
        _savePathBox.Margin = new Padding(0, 0, 8, 0);
        _savePathBox.PaletteMode = PaletteMode.Custom;
        _savePathBox.AccessibleName = "Save location";
        ConfigureInput(_savePathBox);

        ConfigureSecondaryButton(_browseButton, "Browse", (_, _) => BrowseSaveDirectory());
        _browseButton.Dock = DockStyle.Fill;
        _browseButton.Margin = new Padding(0);

        row.Controls.Add(_savePathBox, 0, 0);
        row.Controls.Add(_browseButton, 1, 0);
        section.Controls.Add(row, 0, 1);
        return section;
    }

    private Control BuildPrimaryActionSection()
    {
        ConfigurePrimaryButton(_downloadButton, "Download", async (_, _) => await StartDownloadAsync());
        _downloadButton.Dock = DockStyle.Top;
        _downloadButton.Height = 48;
        _downloadButton.Margin = new Padding(0, 0, 0, 14);
        return _downloadButton;
    }

    private Control BuildProgressSection()
    {
        _progressPanel.Dock = DockStyle.Top;
        _progressPanel.Height = 58;
        _progressPanel.MinimumSize = new Size(0, 58);
        _progressPanel.BackColor = DlpTheme.Surface;
        _progressPanel.ColumnCount = 2;
        _progressPanel.RowCount = 2;
        _progressPanel.Margin = new Padding(0, 0, 0, 10);
        _progressPanel.Padding = new Padding(10, 7, 10, 8);
        _progressPanel.ColumnStyles.Clear();
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        _progressPanel.RowStyles.Clear();
        _progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        _progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));

        _statusLabel.AutoSize = false;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.Text = "Ready to download";
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = DlpTheme.TextSecondary;
        _statusLabel.BackColor = DlpTheme.Surface;
        _statusLabel.Font = new Font(FontFamily, 9F, FontStyle.Regular);

        _progressValueLabel.AutoSize = false;
        _progressValueLabel.Dock = DockStyle.Fill;
        _progressValueLabel.Text = "0%";
        _progressValueLabel.TextAlign = ContentAlignment.MiddleRight;
        _progressValueLabel.ForeColor = DlpTheme.TextPrimary;
        _progressValueLabel.BackColor = DlpTheme.Surface;
        _progressValueLabel.Font = new Font(FontFamily, 9F, FontStyle.Bold);

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Value = 0;
        _progressBar.Height = 6;
        _progressBar.Margin = new Padding(0, 4, 0, 4);

        _progressPanel.Controls.Add(_statusLabel, 0, 0);
        _progressPanel.Controls.Add(_progressValueLabel, 1, 0);
        _progressPanel.Controls.Add(_progressBar, 0, 1);
        _progressPanel.SetColumnSpan(_progressBar, 2);
        return _progressPanel;
    }

    private Control BuildFooterSection()
    {
        TableLayoutPanel footer = new()
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            BackColor = DlpTheme.Bg,
            ColumnCount = 5,
            RowCount = 1,
            Margin = new Padding(0)
        };

        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));

        ConfigureIconButton(_settingsButton, "\uE713", "Check updates", async (_, _) => await UpdateAllAsync());

        Label version = new()
        {
            Text = $"v{Application.ProductVersion.Split('+')[0]}",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = DlpTheme.TextSecondary,
            Font = new Font(FontFamily, 9F, FontStyle.Regular)
        };

        ConfigureInlineButton(_cancelButton, "Cancel", (_, _) => CancelDownload());
        _cancelButton.Visible = false;
        ConfigureSecondaryButton(_openLogButton, "Log", (_, _) => OpenLogFile());
        _openLogButton.Visible = false;
        ConfigureSecondaryButton(_openFolderButton, "Open downloads folder", (_, _) => OpenDownloadFolder());
        _openFolderButton.Dock = DockStyle.Fill;
        _openFolderButton.Margin = new Padding(0, 3, 0, 3);

        footer.Controls.Add(_settingsButton, 0, 0);
        footer.Controls.Add(version, 1, 0);
        footer.Controls.Add(_cancelButton, 3, 0);
        footer.Controls.Add(_openLogButton, 3, 0);
        footer.Controls.Add(_openFolderButton, 4, 0);
        return footer;
    }

    private TableLayoutPanel CreateSection(string title, string iconGlyph, int bottomMargin)
    {
        TableLayoutPanel section = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = DlpTheme.Bg,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, bottomMargin)
        };

        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.Controls.Add(CreateSectionCaption(title, iconGlyph), 0, 0);
        return section;
    }

    private Control CreateSectionCaption(string text, string iconGlyph)
    {
        FlowLayoutPanel caption = new()
        {
            Dock = DockStyle.Top,
            Height = 24,
            BackColor = DlpTheme.Bg,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 4)
        };

        caption.Controls.Add(new Label
        {
            Text = iconGlyph,
            AutoSize = false,
            Width = 24,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = DlpTheme.TextSecondary,
            Font = new Font(IconFontFamily, 12F)
        });

        caption.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = DlpTheme.TextPrimary,
            Font = new Font(FontFamily, 9.5F, FontStyle.Bold),
            Margin = new Padding(0, 2, 0, 0)
        });

        return caption;
    }

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = DlpTheme.TextSecondary,
        BackColor = DlpTheme.Bg,
        Font = new Font(FontFamily, 9F, FontStyle.Bold),
        Margin = new Padding(0)
    };

    private static Label CreateMetaLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = DlpTheme.TextPrimary,
        BackColor = DlpTheme.Surface,
        Font = new Font(FontFamily, 9F, FontStyle.Regular),
        Margin = new Padding(8, 0, 8, 0)
    };

    private static TableLayoutPanel CreateFramedRow(int columnCount, int height, int bottomMargin)
    {
        return new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = height,
            BackColor = DlpTheme.Surface,
            ColumnCount = columnCount,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, bottomMargin),
            Padding = new Padding(2)
        };
    }

    private TableLayoutPanel CreateOptionRow(string title, KryptonToggleSwitch toggle)
    {
        TableLayoutPanel row = new()
        {
            Dock = DockStyle.Fill,
            BackColor = DlpTheme.Bg,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0, 2, 0, 2)
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));

        Label label = new()
        {
            Text = title,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = DlpTheme.TextPrimary,
            BackColor = DlpTheme.Bg,
            Font = new Font(FontFamily, 9.5F, FontStyle.Regular),
            Margin = new Padding(0, 0, 8, 0)
        };

        toggle.Dock = DockStyle.Fill;
        toggle.Margin = new Padding(0, 0, 12, 0);

        row.Controls.Add(label, 0, 0);
        row.Controls.Add(toggle, 1, 0);
        return row;
    }

    private void ConfigureToolTips()
    {
        _toolTips.SetToolTip(_urlBox, "Paste a supported HTTPS media link or drag it here.");
        _toolTips.SetToolTip(_linkButton, "Read link details.");
        _toolTips.SetToolTip(_previewButton, "Open the source link in your browser.");
        _toolTips.SetToolTip(_videoButton, "Download video.");
        _toolTips.SetToolTip(_audioButton, "Download audio.");
        _toolTips.SetToolTip(_qualitySelect, "Video quality limit.");
        _toolTips.SetToolTip(_formatSelect, "Output format.");
        _toolTips.SetToolTip(_embedSubsSwitch, "Embed subtitles when yt-dlp can find them.");
        _toolTips.SetToolTip(_cookiesSwitch, "Use browser cookies for sites that require a session.");
        _toolTips.SetToolTip(_browseButton, "Choose a save folder.");
        _toolTips.SetToolTip(_downloadButton, "Start download.");
        _toolTips.SetToolTip(_settingsButton, "Check DLP and yt-dlp updates.");
        _toolTips.SetToolTip(_openFolderButton, "Open the selected download folder.");
        _toolTips.SetToolTip(_cancelButton, "Stop the active download.");
        _toolTips.SetToolTip(_openLogButton, "Open DLP.log.");
    }

    private YtDlpDownloadOptions GetYtDlpOptions()
    {
        return new YtDlpDownloadOptions(
            _embedSubsSwitch.Checked,
            _cookiesSwitch.Checked,
            CookieBrowserCatalog.Normalize(_browserSelect.SelectedItem?.ToString()) ?? "brave",
            GetSelectedQualityHeight(),
            GetSelectedFormat(),
            ResolveSelectedDownloadDirectory());
    }

    private void UpdateBrowserComboEnabled()
    {
        bool allowBrowser = _cookiesSwitch.Enabled && _cookiesSwitch.Checked;
        _browserSelect.Enabled = allowBrowser;
        _browserSelect.Visible = allowBrowser;
    }

    private void SetOptionControlsEnabled(bool enabled)
    {
        _embedSubsSwitch.Enabled = enabled;
        _cookiesSwitch.Enabled = enabled;
        _qualitySelect.Enabled = enabled && _videoButton.Checked;
        _formatSelect.Enabled = enabled;
        _browseButton.Enabled = enabled;
        _urlBox.Enabled = enabled;
        _linkButton.Enabled = enabled;
        _previewButton.Enabled = enabled && HasUsableSource();
        _videoButton.Enabled = enabled;
        _audioButton.Enabled = enabled;
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
        ConfigureComboBox(_browserSelect);
        _browserSelect.Width = 132;
        _browserSelect.Height = 32;
        _browserSelect.Visible = false;
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
        button.Height = 44;
        button.ButtonStyle = ButtonStyle.Custom1;
        button.PaletteMode = PaletteMode.Custom;
        button.Margin = new Padding(0);
        button.AccessibleName = text.Replace("\uE896", "Download", StringComparison.Ordinal).Trim();
        ApplyKryptonButtonState(button, DlpTheme.AccentActive, DlpTheme.AccentActive, DlpTheme.AccentText, rounding: 0);
        ApplyKryptonButtonState(button.StateTracking, DlpTheme.AccentHover, DlpTheme.AccentHover, DlpTheme.AccentText, rounding: 0);
        ApplyKryptonButtonState(button.StatePressed, DlpTheme.Accent, DlpTheme.BorderStrong, DlpTheme.AccentText, rounding: 0);
        ApplyKryptonButtonState(button.StateDisabled, DlpTheme.SurfaceHover, DlpTheme.Border, DlpTheme.DisabledText, rounding: 0);
        button.Click += handler;
    }

    private static void ConfigureSecondaryButton(KryptonButton button, string text, EventHandler handler)
    {
        button.Text = text;
        button.Height = 38;
        button.ButtonStyle = ButtonStyle.Custom1;
        button.PaletteMode = PaletteMode.Custom;
        button.Margin = new Padding(0);
        button.AccessibleName = text;
        ApplyKryptonButtonState(button, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary, rounding: 0);
        ApplyKryptonButtonState(button.StateTracking, DlpTheme.SurfaceHover, DlpTheme.BorderStrong, DlpTheme.TextPrimary, rounding: 0);
        ApplyKryptonButtonState(button.StatePressed, DlpTheme.Muted, DlpTheme.AccentInteractive, DlpTheme.TextPrimary, rounding: 0);
        ApplyKryptonButtonState(button.StateDisabled, DlpTheme.Surface, DlpTheme.Border, DlpTheme.DisabledText, rounding: 0);
        button.Click += handler;
    }

    private static void ConfigureInlineButton(KryptonButton button, string text, EventHandler handler)
    {
        ConfigureSecondaryButton(button, text, handler);
        button.Width = text.Length > 3 ? 68 : 54;
        button.Height = 34;
        button.Margin = new Padding(4, 4, 4, 4);
    }

    private static void ConfigureIconButton(KryptonButton button, string glyph, string accessibleName, EventHandler handler)
    {
        button.Text = glyph;
        button.Width = IconButtonSize;
        button.Height = IconButtonSize;
        button.ButtonStyle = ButtonStyle.Custom1;
        button.PaletteMode = PaletteMode.Custom;
        button.Margin = new Padding(0, 1, 8, 1);
        button.AccessibleName = accessibleName;
        ApplyKryptonButtonState(button, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary, rounding: 0, font: new Font(IconFontFamily, 12F));
        ApplyKryptonButtonState(button.StateTracking, DlpTheme.SurfaceHover, DlpTheme.BorderStrong, DlpTheme.AccentInteractive, rounding: 0, font: new Font(IconFontFamily, 12F));
        ApplyKryptonButtonState(button.StatePressed, DlpTheme.Muted, DlpTheme.AccentInteractive, DlpTheme.TextPrimary, rounding: 0, font: new Font(IconFontFamily, 12F));
        ApplyKryptonButtonState(button.StateDisabled, DlpTheme.Surface, DlpTheme.Border, DlpTheme.DisabledText, rounding: 0, font: new Font(IconFontFamily, 12F));
        button.Click += handler;
    }

    private static void ConfigureModeButton(KryptonCheckButton button, string text, bool isChecked)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Height = 40;
        button.Checked = isChecked;
        button.ButtonStyle = ButtonStyle.Custom1;
        button.PaletteMode = PaletteMode.Custom;
        button.Margin = new Padding(0);
        button.AccessibleName = text;
        ApplyKryptonButtonState(button, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary, rounding: 0);
        ApplyKryptonButtonState(button.StateTracking, DlpTheme.SurfaceHover, DlpTheme.BorderStrong, DlpTheme.TextPrimary, rounding: 0);
        ApplyKryptonButtonState(button.StatePressed, DlpTheme.Muted, DlpTheme.AccentInteractive, DlpTheme.TextPrimary, rounding: 0);
        ApplyKryptonButtonState(button.StateCheckedNormal, DlpTheme.Muted, DlpTheme.AccentInteractive, DlpTheme.TextPrimary, rounding: 0);
        ApplyKryptonButtonState(button.StateCheckedTracking, DlpTheme.SurfaceHover, DlpTheme.AccentInteractive, DlpTheme.TextPrimary, rounding: 0);
        ApplyKryptonButtonState(button.StateCheckedPressed, DlpTheme.Muted, DlpTheme.AccentInteractive, DlpTheme.TextPrimary, rounding: 0);
        ApplyKryptonButtonState(button.StateDisabled, DlpTheme.Surface, DlpTheme.Border, DlpTheme.DisabledText, rounding: 0);
    }

    private static void ConfigureInput(KryptonTextBox textBox)
    {
        ApplyKryptonInputState(textBox.StateCommon, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary, rounding: 0);
        ApplyKryptonInputState(textBox.StateNormal, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary, rounding: 0);
        ApplyKryptonInputState(textBox.StateActive, DlpTheme.Surface, DlpTheme.AccentInteractive, DlpTheme.TextPrimary, rounding: 0);
        ApplyKryptonInputState(textBox.StateDisabled, DlpTheme.Bg, DlpTheme.Border, DlpTheme.DisabledText, rounding: 0);
    }

    private static void ConfigureComboBox(KryptonComboBox comboBox)
    {
        comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox.PaletteMode = PaletteMode.Custom;
        comboBox.LocalCustomPalette = DlpComboPalette.Instance;
        comboBox.DropButtonStyle = ButtonStyle.InputControl;
        comboBox.Dock = DockStyle.Fill;
        comboBox.Height = 40;
        comboBox.Font = new Font(FontFamily, 9.5F);
        comboBox.ForeColor = DlpTheme.TextPrimary;
        ApplyKryptonInputState(comboBox.StateCommon.ComboBox, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary, rounding: 0);
        comboBox.StateCommon.ComboBox.Content.Padding = new Padding(10, 3, 28, 3);
        comboBox.StateCommon.DropBack.Color1 = DlpTheme.Bg;
        comboBox.StateCommon.DropBack.Color2 = DlpTheme.Bg;
        comboBox.StateCommon.DropBack.ColorStyle = PaletteColorStyle.Solid;
        ApplyKryptonButtonState(comboBox.StateCommon.Item, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary, rounding: 0);
    }

    private static Control WrapComboWithChevron(KryptonComboBox comboBox)
    {
        const int chevronWidth = 28;

        Panel host = new()
        {
            Dock = DockStyle.Fill,
            BackColor = DlpTheme.Bg,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        comboBox.Dock = DockStyle.Fill;
        comboBox.Margin = Padding.Empty;

        Label chevron = new()
        {
            Text = "\uE70D",
            Font = new Font(IconFontFamily, 9F),
            ForeColor = DlpTheme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = false,
            Width = chevronWidth,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            TabStop = false
        };

        void LayoutChevron()
        {
            chevron.Height = host.Height;
            chevron.Location = new Point(Math.Max(0, host.Width - chevronWidth), 0);
            chevron.BringToFront();
        }

        void UpdateChevronColor()
        {
            chevron.ForeColor = comboBox.Enabled
                ? comboBox.DroppedDown ? DlpTheme.TextPrimary : DlpTheme.TextMuted
                : DlpTheme.DisabledText;
        }

        host.Controls.Add(comboBox);
        host.Controls.Add(chevron);
        host.Resize += (_, _) => LayoutChevron();
        comboBox.EnabledChanged += (_, _) => UpdateChevronColor();
        comboBox.DropDown += (_, _) => UpdateChevronColor();
        comboBox.DropDownClosed += (_, _) => UpdateChevronColor();
        chevron.MouseEnter += (_, _) =>
        {
            if (comboBox.Enabled)
            {
                chevron.ForeColor = DlpTheme.TextSecondary;
            }
        };
        chevron.MouseLeave += (_, _) => UpdateChevronColor();
        chevron.Click += (_, _) =>
        {
            if (comboBox.Enabled)
            {
                comboBox.DroppedDown = !comboBox.DroppedDown;
            }
        };

        LayoutChevron();
        UpdateChevronColor();
        return host;
    }

    private static void ConfigureToggleSwitch(KryptonToggleSwitch toggle)
    {
        toggle.Size = new Size(48, 24);
        toggle.MinimumSize = new Size(48, 24);
        toggle.MaximumSize = new Size(54, 28);
        toggle.Cursor = Cursors.Hand;
        toggle.Margin = Padding.Empty;
        toggle.ToggleSwitchValues.ShowText = false;
        toggle.ToggleSwitchValues.UseThemeColors = false;
        toggle.ToggleSwitchValues.OffColor = DlpTheme.BorderStrong;
        toggle.ToggleSwitchValues.OnColor = DlpTheme.AccentActive;
        toggle.ToggleSwitchValues.CornerRadius = 12;
        ApplyKryptonButtonState(toggle.StateCommon, DlpTheme.Surface, DlpTheme.Border, DlpTheme.TextPrimary, rounding: 12);
        ApplyKryptonButtonState(toggle.StateTracking, DlpTheme.SurfaceHover, DlpTheme.BorderStrong, DlpTheme.TextPrimary, rounding: 12);
        ApplyKryptonButtonState(toggle.StatePressed, DlpTheme.Muted, DlpTheme.AccentInteractive, DlpTheme.TextPrimary, rounding: 12);
        ApplyKryptonButtonState(toggle.StateDisabled, DlpTheme.Surface, DlpTheme.Border, DlpTheme.DisabledText, rounding: 12);
    }

    private static void ApplyKryptonInputState(
        PaletteInputControlTripleRedirect state,
        Color backColor,
        Color borderColor,
        Color textColor,
        float rounding)
    {
        state.Back.Color1 = backColor;
        state.Border.Color1 = borderColor;
        state.Border.Color2 = borderColor;
        state.Border.DrawBorders = PaletteDrawBorders.All;
        state.Border.Rounding = rounding;
        state.Border.Width = 1;
        state.Content.Color1 = textColor;
        state.Content.Font = new Font(FontFamily, 9.5F);
        state.Content.Padding = new Padding(10, 7, 10, 7);
    }

    private static void ApplyKryptonInputState(
        PaletteInputControlTripleStates state,
        Color backColor,
        Color borderColor,
        Color textColor,
        float rounding)
    {
        state.Back.Color1 = backColor;
        state.Border.Color1 = borderColor;
        state.Border.Color2 = borderColor;
        state.Border.DrawBorders = PaletteDrawBorders.All;
        state.Border.Rounding = rounding;
        state.Border.Width = 1;
        state.Content.Color1 = textColor;
        state.Content.Font = new Font(FontFamily, 9.5F);
        state.Content.Padding = new Padding(10, 7, 10, 7);
    }

    private static void ApplyKryptonButtonState(
        KryptonButton button,
        Color backColor,
        Color borderColor,
        Color textColor,
        float rounding,
        Font? font = null)
    {
        ApplyKryptonButtonState(button.StateCommon, backColor, borderColor, textColor, rounding, font);
        ApplyKryptonButtonState(button.StateNormal, backColor, borderColor, textColor, rounding, font);
        ApplyKryptonButtonState(button.OverrideDefault, backColor, borderColor, textColor, rounding, font);
        ApplyKryptonButtonState(button.OverrideFocus, backColor, DlpTheme.AccentInteractive, textColor, rounding, font);
    }

    private static void ApplyKryptonButtonState(
        PaletteTripleRedirect state,
        Color backColor,
        Color borderColor,
        Color textColor,
        float rounding,
        Font? font = null)
    {
        state.Back.Color1 = backColor;
        state.Back.Color2 = backColor;
        state.Border.Color1 = borderColor;
        state.Border.Color2 = borderColor;
        state.Border.DrawBorders = PaletteDrawBorders.All;
        state.Border.Rounding = rounding;
        state.Border.Width = 1;
        state.Content.ShortText.Color1 = textColor;
        state.Content.ShortText.Color2 = textColor;
        state.Content.ShortText.Font = font ?? new Font(FontFamily, 9.5F, FontStyle.Bold);
        state.Content.Padding = new Padding(0);
    }

    private static void ApplyKryptonButtonState(
        PaletteTriple state,
        Color backColor,
        Color borderColor,
        Color textColor,
        float rounding,
        Font? font = null)
    {
        state.Back.Color1 = backColor;
        state.Back.Color2 = backColor;
        state.Border.Color1 = borderColor;
        state.Border.Color2 = borderColor;
        state.Border.DrawBorders = PaletteDrawBorders.All;
        state.Border.Rounding = rounding;
        state.Border.Width = 1;
        state.Content.ShortText.Color1 = textColor;
        state.Content.ShortText.Color2 = textColor;
        state.Content.ShortText.Font = font ?? new Font(FontFamily, 9.5F, FontStyle.Bold);
        state.Content.Padding = new Padding(0);
    }
}

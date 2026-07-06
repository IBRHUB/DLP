using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed class DlpProgressBar : Control
{
    private int _value;

    public DlpProgressBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);

        Height = 6;
        BackColor = DlpTheme.Surface;
        FillColor = DlpTheme.AccentActive;
        TrackColor = DlpTheme.Muted;
        AccessibleName = "Download progress";
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    public Color FillColor { get; set; }

    public Color TrackColor { get; set; }

    public int Value
    {
        get => _value;
        set
        {
            int nextValue = Math.Clamp(value, 0, 100);

            if (_value == nextValue)
            {
                return;
            }

            _value = nextValue;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Rectangle bounds = ClientRectangle;

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        int radius = bounds.Height / 2;

        using GraphicsPath trackPath = CreateRoundedRect(bounds, radius);
        using SolidBrush trackBrush = new(TrackColor);
        e.Graphics.FillPath(trackBrush, trackPath);

        if (_value <= 0)
        {
            return;
        }

        int fillWidth = Math.Max(bounds.Height, (int)Math.Round(bounds.Width * (_value / 100d)));
        Rectangle fillBounds = new(bounds.X, bounds.Y, Math.Min(bounds.Width, fillWidth), bounds.Height);

        using GraphicsPath fillPath = CreateRoundedRect(fillBounds, radius);
        using SolidBrush fillBrush = new(FillColor);
        e.Graphics.FillPath(fillBrush, fillPath);
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();
        int diameter = radius * 2;
        Rectangle arc = new(bounds.Location, new Size(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

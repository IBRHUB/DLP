using System.Drawing;
using System.Windows.Forms;

internal sealed class DlpToggleButton : CheckBox
{
    private bool _hovered;
    private bool _pressed;

    public DlpToggleButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);

        Appearance = Appearance.Button;
        AutoSize = false;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 8F, FontStyle.Bold);
        Size = new Size(54, 26);
        MinimumSize = Size;
        MaximumSize = Size;
        TextAlign = ContentAlignment.MiddleCenter;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Rectangle bounds = ClientRectangle;

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        Color backColor = ResolveBackColor();
        Color borderColor = ResolveBorderColor();
        Color textColor = ResolveTextColor();

        using SolidBrush backBrush = new(backColor);
        e.Graphics.FillRectangle(backBrush, bounds);

        using Pen borderPen = new(borderColor);
        e.Graphics.DrawRectangle(borderPen, 0, 0, bounds.Width - 1, bounds.Height - 1);

        string label = Checked ? "ON" : "OFF";
        TextRenderer.DrawText(
            e.Graphics,
            label,
            Font,
            bounds,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (Focused && ShowFocusCues)
        {
            Rectangle focusBounds = Rectangle.Inflate(bounds, -3, -3);
            ControlPaint.DrawFocusRectangle(e.Graphics, focusBounds, textColor, backColor);
        }
    }

    private Color ResolveBackColor()
    {
        if (!Enabled)
        {
            return DlpTheme.Surface;
        }

        if (Checked)
        {
            return _pressed ? DlpTheme.AccentHover : DlpTheme.AccentActive;
        }

        return _hovered || _pressed ? DlpTheme.SurfaceHover : DlpTheme.Surface;
    }

    private Color ResolveBorderColor()
    {
        if (!Enabled)
        {
            return DlpTheme.Border;
        }

        return Checked ? DlpTheme.AccentActive : DlpTheme.BorderStrong;
    }

    private Color ResolveTextColor()
    {
        if (!Enabled)
        {
            return DlpTheme.DisabledText;
        }

        return Checked ? DlpTheme.AccentText : DlpTheme.TextSecondary;
    }
}

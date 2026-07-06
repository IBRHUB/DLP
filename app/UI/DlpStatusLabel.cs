using System.Drawing;
using System.Windows.Forms;

internal sealed class DlpStatusLabel : Control
{
    public DlpStatusLabel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);

        BackColor = DlpTheme.Bg;
        AccessibleRole = AccessibleRole.StatusBar;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        Invalidate();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

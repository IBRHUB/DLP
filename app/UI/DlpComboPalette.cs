using System.Drawing;
using Krypton.Toolkit;

internal static class DlpComboPalette
{
    private static readonly KryptonCustomPaletteBase Palette = Create();

    public static KryptonCustomPaletteBase Instance => Palette;

    private static KryptonCustomPaletteBase Create()
    {
        KryptonCustomPaletteBase palette = new();
        ConfigureDropButton(palette.ButtonStyles.ButtonInputControl);
        return palette;
    }

    private static void ConfigureDropButton(KryptonPaletteCheckButton button)
    {
        ApplyDropButtonState(button.StateCommon, DlpTheme.Surface);
        ApplyDropButtonState(button.StateNormal, DlpTheme.Surface);
        ApplyDropButtonState(button.StateTracking, DlpTheme.Surface);
        ApplyDropButtonState(button.StatePressed, DlpTheme.Surface);
        ApplyDropButtonState(button.StateDisabled, DlpTheme.Surface);
        ApplyDropButtonState(button.StateCheckedNormal, DlpTheme.Surface);
        ApplyDropButtonState(button.StateCheckedTracking, DlpTheme.Surface);
        ApplyDropButtonState(button.StateCheckedPressed, DlpTheme.Surface);
    }

    private static void ApplyDropButtonState(PaletteTripleRedirect state, Color backColor)
    {
        state.Back.Color1 = backColor;
        state.Back.Color2 = backColor;
        state.Back.ColorStyle = PaletteColorStyle.Solid;
        state.Border.DrawBorders = PaletteDrawBorders.None;
        state.Content.ShortText.Color1 = backColor;
        state.Content.ShortText.Color2 = backColor;
        state.Content.Padding = Padding.Empty;
    }

    private static void ApplyDropButtonState(PaletteTriple state, Color backColor)
    {
        state.Back.Color1 = backColor;
        state.Back.Color2 = backColor;
        state.Back.ColorStyle = PaletteColorStyle.Solid;
        state.Border.DrawBorders = PaletteDrawBorders.None;
        state.Content.ShortText.Color1 = backColor;
        state.Content.ShortText.Color2 = backColor;
        state.Content.Padding = Padding.Empty;
    }
}

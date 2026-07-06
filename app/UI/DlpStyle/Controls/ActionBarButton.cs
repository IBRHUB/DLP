using System.Windows;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;

namespace DLP.UI.DlpStyle.Controls;

public class ActionBarButton : WpfButton
{
    static ActionBarButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ActionBarButton), new FrameworkPropertyMetadata(typeof(ActionBarButton)));
    }

    public static readonly DependencyProperty IconPathProperty = DependencyProperty.Register(
        nameof(IconPath),
        typeof(Geometry),
        typeof(ActionBarButton),
        new PropertyMetadata(null));

    public Geometry? IconPath
    {
        get => (Geometry?)GetValue(IconPathProperty);
        set => SetValue(IconPathProperty, value);
    }

    public static readonly DependencyProperty IconWidthProperty = DependencyProperty.Register(
        nameof(IconWidth),
        typeof(double),
        typeof(ActionBarButton),
        new PropertyMetadata(14d));

    public double IconWidth
    {
        get => (double)GetValue(IconWidthProperty);
        set => SetValue(IconWidthProperty, value);
    }

    public static readonly DependencyProperty IconHeightProperty = DependencyProperty.Register(
        nameof(IconHeight),
        typeof(double),
        typeof(ActionBarButton),
        new PropertyMetadata(14d));

    public double IconHeight
    {
        get => (double)GetValue(IconHeightProperty);
        set => SetValue(IconHeightProperty, value);
    }

    public static readonly DependencyProperty IconMarginProperty = DependencyProperty.Register(
        nameof(IconMargin),
        typeof(Thickness),
        typeof(ActionBarButton),
        new PropertyMetadata(new Thickness(0, 0, 8, 0)));

    public Thickness IconMargin
    {
        get => (Thickness)GetValue(IconMarginProperty);
        set => SetValue(IconMarginProperty, value);
    }

    public static readonly DependencyProperty IconFillProperty = DependencyProperty.Register(
        nameof(IconFill),
        typeof(WpfBrush),
        typeof(ActionBarButton),
        new PropertyMetadata(null));

    public WpfBrush? IconFill
    {
        get => (WpfBrush?)GetValue(IconFillProperty);
        set => SetValue(IconFillProperty, value);
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(ActionBarButton),
        new PropertyMetadata(string.Empty));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty TextFontSizeProperty = DependencyProperty.Register(
        nameof(TextFontSize),
        typeof(double),
        typeof(ActionBarButton),
        new PropertyMetadata(12d));

    public double TextFontSize
    {
        get => (double)GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
    }

    public static readonly DependencyProperty ShowIconProperty = DependencyProperty.Register(
        nameof(ShowIcon),
        typeof(bool),
        typeof(ActionBarButton),
        new PropertyMetadata(true));

    public bool ShowIcon
    {
        get => (bool)GetValue(ShowIconProperty);
        set => SetValue(ShowIconProperty, value);
    }
}

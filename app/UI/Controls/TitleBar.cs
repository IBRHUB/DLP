using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using WpfButton = System.Windows.Controls.Button;
using WpfControl = System.Windows.Controls.Control;

namespace DLP.UI.Controls;

public class TitleBar : WpfControl
{
    static TitleBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TitleBar), new FrameworkPropertyMetadata(typeof(TitleBar)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("MinimizeButton") is WpfButton minimizeButton)
        {
            minimizeButton.Click += (_, _) => ParentWindow.WindowState = WindowState.Minimized;
        }

        if (GetTemplateChild("CloseButton") is WpfButton closeButton)
        {
            closeButton.Click += (_, _) => ParentWindow.Close();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (IsInsideWindowButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        Window window = ParentWindow;
        if (window.WindowState == WindowState.Normal)
        {
            window.DragMove();
            e.Handled = true;
        }
    }

    private static bool IsInsideWindowButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is WpfButton)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private Window ParentWindow
    {
        get
        {
            DependencyObject parent = this;
            while (VisualTreeHelper.GetParent(parent) is { } next && parent is not Window)
            {
                parent = next;
            }

            return (Window)parent;
        }
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(TitleBar),
        new PropertyMetadata("DLP"));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty LeftContentProperty = DependencyProperty.Register(
        nameof(LeftContent),
        typeof(object),
        typeof(TitleBar),
        new PropertyMetadata(null));

    public object? LeftContent
    {
        get => GetValue(LeftContentProperty);
        set => SetValue(LeftContentProperty, value);
    }
}

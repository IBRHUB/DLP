using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DLP.UI.Converters;

public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = string.Equals(parameter?.ToString(), "Invert", StringComparison.OrdinalIgnoreCase);
        bool visible = string.IsNullOrEmpty(value as string);
        return visible ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

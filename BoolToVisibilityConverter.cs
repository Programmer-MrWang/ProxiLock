using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ProxiLock;

/// <summary>Shows an element when the bound boolean is true, collapses it otherwise.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}

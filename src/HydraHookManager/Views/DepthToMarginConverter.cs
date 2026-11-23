using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HydraHookManager.Views;

/// <summary>
///     Indents a tree row by 16 device-independent pixels per depth level.
/// </summary>
public sealed class DepthToMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int depth = value is int number && number > 0 ? number : 0;
        return new Thickness(depth * 16, 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
///     Collapses a control when the bound flag is true.
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

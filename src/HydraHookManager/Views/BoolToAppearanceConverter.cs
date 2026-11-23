using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace HydraHookManager.Views;

/// <summary>
///     Maps a pressed toggle to the primary button appearance and a released toggle to secondary.
/// </summary>
public sealed class BoolToAppearanceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

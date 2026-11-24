using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HydraHookManager.Models;

namespace HydraHookManager.Views;

/// <summary>
///     Shows the shield or lock icon for one <see cref="ProcessAccessState"/>.
///     ConverterParameter is "admin" or "protected".
/// </summary>
public sealed class AccessStateVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProcessAccessState state)
        {
            return Visibility.Collapsed;
        }

        bool visible = (parameter as string) switch
        {
            "admin" => state == ProcessAccessState.RequiresAdmin,
            "protected" => state == ProcessAccessState.Protected,
            _ => false
        };
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
///     Shows the injection pill when a process is verified or still tracked.
/// </summary>
public sealed class InjectionBadgeVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is InjectionBadgeState state && state != InjectionBadgeState.None
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
///     "Injected" for a verified module match, otherwise "Tracked".
/// </summary>
public sealed class InjectionBadgeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is InjectionBadgeState.Verified ? "Injected"
            : value is InjectionBadgeState.Stopped ? "Stopped"
            : "Tracked";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
///     Green tint for a verified injection and amber tint for an unverified one.
/// </summary>
public sealed class InjectionBadgeBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Verified = Create(Color.FromArgb(0x44, 0x3C, 0xB0, 0x6B));
    private static readonly SolidColorBrush Tracked = Create(Color.FromArgb(0x44, 0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush Stopped = Create(Color.FromArgb(0x55, 0xE1, 0x5D, 0x4A));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            InjectionBadgeState.Verified => Verified,
            InjectionBadgeState.Stopped => Stopped,
            _ => Tracked
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static SolidColorBrush Create(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}

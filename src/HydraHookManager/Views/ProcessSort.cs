using System.Windows;

namespace HydraHookManager;

/// <summary>
///     Attached properties that tie a list column to a sort key and its header label.
/// </summary>
public static class ProcessSort
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached("Key", typeof(string), typeof(ProcessSort));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.RegisterAttached("Label", typeof(string), typeof(ProcessSort));

    public static void SetKey(DependencyObject element, string? value)
    {
        element.SetValue(KeyProperty, value);
    }

    public static string? GetKey(DependencyObject element)
    {
        return (string?)element.GetValue(KeyProperty);
    }

    public static void SetLabel(DependencyObject element, string? value)
    {
        element.SetValue(LabelProperty, value);
    }

    public static string? GetLabel(DependencyObject element)
    {
        return (string?)element.GetValue(LabelProperty);
    }
}

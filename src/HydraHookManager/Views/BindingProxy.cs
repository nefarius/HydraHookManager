using System.Windows;

namespace HydraHookManager.Views;

/// <summary>
///     Holds a binding for elements outside the window namescope, such as a context-menu submenu.
///     An <c>x:Reference</c> from that submenu back to its own menu is a XAML cycle and the window will not load.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data),
        typeof(object),
        typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore()
    {
        return new BindingProxy();
    }
}

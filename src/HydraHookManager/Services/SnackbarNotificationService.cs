using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace HydraHookManager.Services;

public sealed class SnackbarNotificationService(ISnackbarService snackbarService) : INotificationService
{
    public void Show(string title, string message, NotificationSeverity severity, TimeSpan? timeout = null)
    {
        ControlAppearance appearance = severity switch
        {
            NotificationSeverity.Success => ControlAppearance.Success,
            NotificationSeverity.Warning => ControlAppearance.Caution,
            NotificationSeverity.Error => ControlAppearance.Danger,
            _ => ControlAppearance.Secondary
        };

        if (timeout is { } duration)
        {
            snackbarService.Show(title, message, appearance, null, duration);
        }
        else
        {
            snackbarService.Show(title, message, appearance);
        }
    }
}

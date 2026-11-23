namespace HydraHookManager.Services;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public interface INotificationService
{
    void Show(string title, string message, NotificationSeverity severity, TimeSpan? timeout = null);
}

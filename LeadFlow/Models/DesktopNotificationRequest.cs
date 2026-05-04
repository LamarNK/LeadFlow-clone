namespace LeadFlow.Models;

public sealed class DesktopNotificationRequest
{
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DesktopNotificationSeverity Severity { get; init; } = DesktopNotificationSeverity.Info;
    public int TimeoutMilliseconds { get; init; } = 5000;
}

public enum DesktopNotificationSeverity
{
    Info,
    Warning,
    Error
}

namespace Orbita.Api.Options;

public sealed class CrmDeadlineNotificationOptions
{
    public const string SectionName = "CrmDeadlineNotifications";

    public int ScanIntervalSeconds { get; set; } = 60;
    public int FirstReminderMinutes { get; set; } = 24 * 60;
    public int FinalReminderMinutes { get; set; } = 60;
    public int BatchSize { get; set; } = 200;
    public int RetentionDays { get; set; } = 90;
}

namespace Orbita.Api.Options;

public sealed class ServiceLogsOptions
{
    public const string SectionName = "ServiceLogs";

    public int RetentionDays { get; set; } = 365;
    public int CleanupIntervalHours { get; set; } = 24;
}
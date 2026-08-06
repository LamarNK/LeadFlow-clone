namespace Orbita.Api.Options;

public sealed class WorkerLogsOptions
{
    public const string SectionName = "WorkerLogs";

    public int RetentionDays { get; set; } = 30;
    public int CleanupIntervalHours { get; set; } = 24;
    public int MaxBatchSize { get; set; } = 2000;
}
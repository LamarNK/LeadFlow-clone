namespace Orbita.Api.Options;

public sealed class WorkerDiagnosticsOptions
{
    public const string SectionName = "WorkerDiagnostics";

    public string DataPath { get; set; } = "Data/diagnostics";

    public long MaxUploadBytes { get; set; } = 2_097_152;

    /// <summary>Скриншоты старше этого срока удаляются (файл + запись в БД).</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>Интервал фоновой очистки устаревших скриншотов.</summary>
    public int CleanupIntervalHours { get; set; } = 6;
}
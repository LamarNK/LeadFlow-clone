namespace Orbita.Worker;

internal static class WorkerMaintenanceOptions
{
    public const int LogRetentionDays = 30;
    public const int LogCleanupIntervalHours = 24;

    /// <summary>Локальные отклики старше этого срока удаляются (дедуп на API остаётся источником истины).</summary>
    public const int CandidateRetentionDays = 90;

    /// <summary>Интервал фоновой очистки worker.db.</summary>
    public const int DatabaseCleanupIntervalHours = 24;

    /// <summary>Локальный кэш дедупа при недоступности API.</summary>
    public const int DedupCacheRetentionDays = 7;
}
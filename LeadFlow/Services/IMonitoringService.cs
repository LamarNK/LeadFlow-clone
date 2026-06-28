

namespace LeadFlow.Services;

public interface IMonitoringService
{
    event EventHandler<MonitoringStatus>? StatusChanged;
    event EventHandler<string>? StatusMessageChanged;
    event EventHandler<CandidateResponse>? ResponseProcessed;
    event EventHandler<ProfileStatsUpdatedEventArgs>? ProfileStatsUpdated;
    event EventHandler? NextCycleCheckTimeChanged;
    event EventHandler<string>? MonitoringAutoStopped;
    MonitoringStatus CurrentStatus { get; }
    string CurrentStatusMessage { get; }
    bool IsActive { get; }
    DateTime? NextCycleCheckAtUtc { get; }
    /// <summary>
    /// Объединённый список активных объявлений: по одному снимку на каждый аккаунт в памяти мониторинга
    /// (ключ — <see cref="AvitoAccount.Id"/>), без «глобальной» подмены всего списка одним проходом.
    /// </summary>
    IReadOnlyList<AvitoAdStatus> GetActiveAdsSnapshot();

    /// <summary>
    /// Срез заблокированных объявлений (вкладка «С ошибками» Avito Pro) по всем аккаунтам.
    /// Используется дашбордом, чтобы показать список и подкорректировать поведение бота.
    /// </summary>
    IReadOnlyList<AvitoAdStatus> GetBlockedAdsSnapshot();

    /// <summary>
    /// Восстанавливает в памяти снимки объявлений из полей <c>ActiveAdsSnapshotJson</c> / <c>BlockedAdsSnapshotJson</c>
    /// загруженных аккаунтов (например после чтения из БД при старте UI).
    /// </summary>
    void RestorePersistedAdSnapshots(IReadOnlyList<AvitoAccount> accounts);

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

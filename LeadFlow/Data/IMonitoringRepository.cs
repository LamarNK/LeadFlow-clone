using LeadFlow.Models;

namespace LeadFlow.Data;

/// <summary>
/// Узкая абстракция над репозиторием для операций главного цикла мониторинга.
/// Позволяет тестировать <c>MonitoringService</c> без поднятия SQLite-инфраструктуры EF Core.
/// </summary>
public interface IMonitoringRepository
{
    Task<IReadOnlyList<AvitoAccount>> GetAccountsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AvitoAccount>> GetAdSnapshotAccountsAsync(CancellationToken cancellationToken);
    Task SaveAccountAsync(AvitoAccount account, CancellationToken cancellationToken);
    Task SaveCandidateAsync(CandidateResponse response, CancellationToken cancellationToken);
    Task AddLogAsync(ProcessingLogItem item, CancellationToken cancellationToken);
    Task<HashSet<string>> GetExistingBitrixEntityIdsAsync(IEnumerable<string> bitrixEntityIds, CancellationToken cancellationToken);

    /// <summary>0…1 — насколько текущий момент (локальный день/час ПК) исторически совпадает с частыми приходами новых откликов в БД.</summary>
    Task<double> GetHistoricalResponseIngestHeatScoreAsync(DateTime utcNow, CancellationToken cancellationToken);
}

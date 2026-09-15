using LeadFlow.Core.Data;
using LeadFlow.Core.Models;

namespace Orbita.Worker.Services;

/// <summary>
/// Account state in memory; stats/heat from Orbita API.
/// </summary>
public sealed class OrbitaMonitoringRepository(
    WorkerAccountRuntimeStore runtimeStore,
    OrbitaApiClient apiClient) : IMonitoringRepository
{
    public Task<IReadOnlyList<AvitoAccount>> GetAccountsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AvitoAccount>>(runtimeStore.GetAll());

    public Task<IReadOnlyList<AvitoAccount>> GetAdSnapshotAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = runtimeStore.GetAll()
            .Select(static account => new AvitoAccount
            {
                Id = account.Id,
                ActiveAdsSnapshotJson = account.ActiveAdsSnapshotJson,
                BlockedAdsSnapshotJson = account.BlockedAdsSnapshotJson,
                UnpublishedAdsSnapshotJson = account.UnpublishedAdsSnapshotJson
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<AvitoAccount>>(accounts);
    }

    public Task SaveAccountAsync(AvitoAccount account, CancellationToken cancellationToken)
    {
        runtimeStore.Upsert(account);
        return Task.CompletedTask;
    }

    public Task SaveCandidateAsync(CandidateResponse response, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task AddLogAsync(ProcessingLogItem item, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<HashSet<string>> GetExistingBitrixEntityIdsAsync(
        IEnumerable<string> bitrixEntityIds,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public async Task<double> GetHistoricalResponseIngestHeatScoreAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var stats = await apiClient.GetMonitoringStatsAsync(cancellationToken).ConfigureAwait(false);
        return stats?.HistoricalHeatScore ?? 0;
    }
}

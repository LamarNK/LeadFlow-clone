using LeadFlow.Data;
using LeadFlow.Models;

namespace LeadFlow.Tests.Support;

internal sealed class FakeMonitoringRepository : IMonitoringRepository
{
    public List<AvitoAccount> SavedAccounts { get; } = new();
    public List<CandidateResponse> SavedCandidates { get; } = new();
    public List<ProcessingLogItem> Logs { get; } = new();

    /// <summary>Порядок вызовов SaveCandidate / AddLog для проверок в тестах.</summary>
    public List<string> OperationTrace { get; } = new();
    public Func<IReadOnlyList<AvitoAccount>> AccountsImpl { get; set; } = () => Array.Empty<AvitoAccount>();
    public Func<IEnumerable<string>, HashSet<string>> ExistingBitrixEntityIdsImpl { get; set; } =
        _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<AvitoAccount>> GetAccountsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(AccountsImpl());

    public Task SaveAccountAsync(AvitoAccount account, CancellationToken cancellationToken)
    {
        SavedAccounts.Add(account);
        return Task.CompletedTask;
    }

    public Task SaveCandidateAsync(CandidateResponse response, CancellationToken cancellationToken)
    {
        OperationTrace.Add($"Save:{response.Status}:{response.BitrixEntityId}");
        SavedCandidates.Add(response);
        return Task.CompletedTask;
    }

    public Task AddLogAsync(ProcessingLogItem item, CancellationToken cancellationToken)
    {
        OperationTrace.Add($"Log:{item.Message}");
        Logs.Add(item);
        return Task.CompletedTask;
    }

    public Task<HashSet<string>> GetExistingBitrixEntityIdsAsync(
        IEnumerable<string> bitrixEntityIds, CancellationToken cancellationToken) =>
        Task.FromResult(ExistingBitrixEntityIdsImpl(bitrixEntityIds));
}

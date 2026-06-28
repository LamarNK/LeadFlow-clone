using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Worker;

public interface IWorkerConfigProvider
{
    Task<WorkerMonitoringConfig> GetConfigAsync(CancellationToken cancellationToken);
}

public sealed class WorkerMonitoringConfig
{
    public DuplicateScope DuplicateScope { get; init; } = DuplicateScope.GlobalAcrossAllAccounts;
    public int MaxConcurrentAccounts { get; init; } = 1;
    public bool DemoModeEnabled { get; init; }
    public IReadOnlyList<AvitoAccount> Accounts { get; init; } = [];
}
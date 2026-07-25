using LeadFlow.Core.Models;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

public interface IWorkerConfigProvider
{
    Task<WorkerMonitoringConfig> GetConfigAsync(CancellationToken cancellationToken);

    /// <summary>Сбросить кэш конфига (Orbita API), чтобы подхватить настройки без ожидания следующего цикла.</summary>
    void InvalidateConfigCache()
    {
    }
}

public sealed class WorkerMonitoringConfig
{
    public DuplicateScope DuplicateScope { get; init; } = DuplicateScope.GlobalAcrossAllAccounts;
    public int MaxConcurrentAccounts { get; init; } = 1;
    public bool DemoModeEnabled { get; init; }
    public IReadOnlyList<AvitoAccount> Accounts { get; init; } = [];
    public ResponseCollectionFilters ResponseFilters { get; init; } = ResponseCollectionFilters.Disabled;
    public AvitoMessengerAutoReplySettings? MessengerAutoReply { get; init; }
}
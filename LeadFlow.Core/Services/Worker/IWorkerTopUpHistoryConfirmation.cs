using LeadFlow.Core.Services.Avito;

namespace LeadFlow.Core.Services.Worker;

public interface IWorkerTopUpHistoryConfirmation
{
    Task<int> ConfirmAsync(
        Guid workerId,
        Guid accountId,
        string subProfileId,
        IReadOnlyList<AvitoWalletHistoryOperation> operations,
        CancellationToken cancellationToken = default);
}

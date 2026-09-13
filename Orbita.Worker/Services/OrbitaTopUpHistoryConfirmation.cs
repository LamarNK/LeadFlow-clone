using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class OrbitaTopUpHistoryConfirmation(
    OrbitaApiClient apiClient) : IWorkerTopUpHistoryConfirmation
{
    public async Task<int> ConfirmAsync(
        Guid workerId,
        Guid accountId,
        string subProfileId,
        IReadOnlyList<AvitoWalletHistoryOperation> operations,
        decimal? advanceBalance = null,
        CancellationToken cancellationToken = default)
    {
        _ = workerId;
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return 0;
        }

        var result = await apiClient.ConfirmTopUpHistoryAsync(
                new ConfirmTopUpHistoryRequest(
                    accountId,
                    subProfileId,
                    DateTime.UtcNow,
                    operations
                        .Select(x => new TopUpHistoryOperationDto(x.Amount, x.OccurredAtUtc, x.Description))
                        .ToList(),
                    advanceBalance),
                cancellationToken)
            .ConfigureAwait(false);
        return result?.ConfirmedCount ?? 0;
    }
}

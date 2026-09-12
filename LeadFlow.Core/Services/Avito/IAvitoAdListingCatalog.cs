using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

public interface IAvitoAdListingCatalog
{
    Task<IReadOnlyList<AvitoAdListingRecord>> GetAccountListingsAsync(
        Guid workerId,
        Guid accountId,
        CancellationToken cancellationToken);

    Task SaveSubProfileSyncAsync(
        Guid workerId,
        Guid accountId,
        string avitoSubProfileId,
        IReadOnlyList<AvitoAdListingRecord> records,
        bool listComplete,
        DateTime capturedAtUtc,
        CancellationToken cancellationToken);
}

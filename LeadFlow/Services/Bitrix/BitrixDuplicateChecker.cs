using LeadFlow.Models;

namespace LeadFlow.Services.Bitrix;

public sealed class BitrixDuplicateChecker(IBitrixClient client)
{
    public Task<BitrixDuplicateLookupResult> HasDuplicateAsync(string phoneNormalized, AppSettings settings, CancellationToken cancellationToken) =>
        client.HasDuplicateAsync(phoneNormalized, settings, cancellationToken);
}

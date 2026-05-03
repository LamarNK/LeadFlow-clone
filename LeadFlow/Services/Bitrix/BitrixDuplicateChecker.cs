using LeadFlow.Models;

namespace LeadFlow.Services.Bitrix;

public sealed class BitrixDuplicateChecker(IBitrixClient client)
{
    public Task<bool> HasDuplicateAsync(string phoneNormalized, AppSettings settings, CancellationToken cancellationToken) =>
        client.HasDuplicateAsync(phoneNormalized, settings, cancellationToken);
}

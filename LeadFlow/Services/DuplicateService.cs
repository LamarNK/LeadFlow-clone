using LeadFlow.Data;
using LeadFlow.Models;
using LeadFlow.Services.Bitrix;

namespace LeadFlow.Services;

public sealed class DuplicateService(
    AppRepository repository,
    IPhoneNormalizer phoneNormalizer,
    IBitrixClient bitrixClient) : IDuplicateService
{
    public async Task<DuplicateCheckResult> CheckAsync(CandidateResponse response, AppSettings settings, CancellationToken cancellationToken)
    {
        var normalized = phoneNormalizer.Normalize(response.PhoneRaw);
        response.PhoneNormalized = normalized;

        var local = await repository.FindDuplicateAsync(normalized, settings.DuplicateScope, response.AccountId, cancellationToken);
        var bitrix = settings.Bitrix.CheckDuplicatesInBitrix
            ? await bitrixClient.HasDuplicateAsync(normalized, settings, cancellationToken)
            : false;

        return new DuplicateCheckResult
        {
            PhoneRaw = response.PhoneRaw,
            PhoneNormalized = normalized,
            IsLocalDuplicate = local is not null && local.Id != response.Id,
            IsBitrixDuplicate = bitrix
        };
    }
}

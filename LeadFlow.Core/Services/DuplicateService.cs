using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Bitrix;

namespace LeadFlow.Core.Services;

public sealed class DuplicateService(
    ICandidateDuplicateRepository duplicateRepository,
    IPhoneNormalizer phoneNormalizer,
    IBitrixClient bitrixClient) : IDuplicateService
{
    public async Task<DuplicateCheckResult> CheckAsync(CandidateResponse response, AppSettings settings, CancellationToken cancellationToken)
    {
        var normalized = phoneNormalizer.Normalize(response.PhoneRaw);
        response.PhoneNormalized = normalized;

        CandidateResponse? local = null;
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            local = await duplicateRepository.FindDuplicateAsync(normalized, settings.DuplicateScope, response.AccountId, cancellationToken);
        }

        var isBitrixDuplicate = false;
        var bitrixUnavailable = false;
        string? bitrixUnavailableReason = null;

        if (settings.Bitrix.CheckDuplicatesInBitrix)
        {
            var bitrixLookup = await bitrixClient.HasDuplicateAsync(normalized, settings, cancellationToken);
            if (bitrixLookup.IsUnavailable)
            {
                bitrixUnavailable = true;
                bitrixUnavailableReason = bitrixLookup.ErrorMessage;
            }
            else if (!bitrixLookup.IsSkipped)
            {
                isBitrixDuplicate = bitrixLookup.IsDuplicate;
            }
        }

        return new DuplicateCheckResult
        {
            PhoneRaw = response.PhoneRaw,
            PhoneNormalized = normalized,
            IsLocalDuplicate = local is not null && local.Id != response.Id,
            IsBitrixDuplicate = isBitrixDuplicate,
            IsBitrixCheckUnavailable = bitrixUnavailable,
            BitrixCheckUnavailableReason = bitrixUnavailableReason
        };
    }
}

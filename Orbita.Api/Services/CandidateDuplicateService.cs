using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services.Bitrix;

namespace Orbita.Api.Services;

public sealed class CandidateDuplicateService(
    OrbitaDbContext db,
    BitrixClient bitrixClient)
{
    public async Task<CandidateResponseEntity?> FindLocalDuplicateAsync(
        Guid officeId,
        string phoneNormalized,
        Guid excludeId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNormalized))
        {
            return null;
        }

        var duplicateCutoffUtc = CandidateDuplicateLookback.GetCutoffUtc(DateTime.UtcNow);

        return await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.OfficeId == officeId
                        && x.PhoneNormalized == phoneNormalized
                        && x.CreatedAt >= duplicateCutoffUtc
                        && x.Id != excludeId)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<DuplicateCheckResult> CheckAsync(
        CandidateResponseEntity response,
        string? webhookUrl,
        bool checkDuplicatesInBitrix,
        CancellationToken ct = default)
    {
        var result = new DuplicateCheckResult
        {
            PhoneRaw = response.PhoneRaw,
            PhoneNormalized = response.PhoneNormalized
        };

        var local = await FindLocalDuplicateAsync(
            response.OfficeId,
            response.PhoneNormalized,
            response.Id,
            ct);
        result.IsLocalDuplicate = local is not null;

        if (!checkDuplicatesInBitrix)
        {
            return result;
        }

        var bitrixLookup = await bitrixClient.HasDuplicateAsync(response.PhoneNormalized, webhookUrl, ct);
        if (bitrixLookup.IsUnavailable)
        {
            result.IsBitrixCheckUnavailable = true;
            result.BitrixCheckUnavailableReason = bitrixLookup.ErrorMessage;
        }
        else if (!bitrixLookup.IsSkipped)
        {
            result.IsBitrixDuplicate = bitrixLookup.IsDuplicate;
        }

        return result;
    }
}
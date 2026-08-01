using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CandidateDuplicateService(
    OrbitaDbContext db,
    CandidatePersonMatchService personMatch,
    BitrixClient bitrixClient)
{
    public async Task<CandidateResponseEntity?> FindLocalDuplicateAsync(
        Guid? officeId,
        Guid personId,
        Guid excludeId,
        CancellationToken ct = default)
    {
        if (personId == Guid.Empty)
        {
            return null;
        }

        var duplicateCutoffUtc = CandidateDuplicateLookback.GetCutoffUtc(DateTime.UtcNow);
        var query = db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.PersonId == personId
                        && x.Id != excludeId
                        && x.CreatedAt >= duplicateCutoffUtc);
        if (officeId is Guid oid)
        {
            query = query.Where(x => x.OfficeId == null || x.OfficeId == oid);
        }

        return await query
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<CandidatePersonEntity?> FindMatchingPersonAsync(
        Guid? officeId,
        CandidateMatchProfile profile,
        CancellationToken ct = default) =>
        await personMatch.FindMatchingPersonAsync(officeId, profile, ct);

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
            response.PersonId,
            response.Id,
            ct);
        result.IsLocalDuplicate = local is not null;

        if (!checkDuplicatesInBitrix)
        {
            return result;
        }

        var profile = CandidatePersonMatchService.ToProfile(response);
        var bitrixLookup = await bitrixClient.HasDuplicateAsync(profile, webhookUrl, ct);
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
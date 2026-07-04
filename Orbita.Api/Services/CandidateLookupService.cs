using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CandidateLookupService(OrbitaDbContext db)
{
    public async Task<WorkerCandidateLookupResponse?> LookupAsync(
        Guid workerId,
        WorkerCandidateLookupRequest request,
        CancellationToken ct = default)
    {
        var worker = await db.Workers
            .AsNoTracking()
            .Where(x => x.Id == workerId)
            .Select(x => new { x.OfficeId })
            .FirstOrDefaultAsync(ct);
        if (worker is null)
        {
            return null;
        }

        var sourceIds = request.SourceResponseIds
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var phones = request.PhoneNormalized
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var existingSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sourceIds.Length > 0)
        {
            var matched = await db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.OfficeId == worker.OfficeId
                            && x.AccountId == request.AccountId
                            && sourceIds.Contains(x.SourceResponseId))
                .Select(x => x.SourceResponseId)
                .ToListAsync(ct);
            foreach (var id in matched)
            {
                existingSourceIds.Add(id);
            }
        }

        var existingPhones = new HashSet<string>(StringComparer.Ordinal);
        var perAccount = string.Equals(
            request.DuplicateScope,
            "PerAvitoAccount",
            StringComparison.OrdinalIgnoreCase);

        if (phones.Length > 0 || request.IncludeAllKnownPhones)
        {
            var phoneQuery = db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.OfficeId == worker.OfficeId && x.PhoneNormalized != "");
            if (perAccount)
            {
                phoneQuery = phoneQuery.Where(x => x.AccountId == request.AccountId);
            }

            if (!request.IncludeAllKnownPhones)
            {
                phoneQuery = phoneQuery.Where(x => phones.Contains(x.PhoneNormalized));
            }

            var matchedPhones = await phoneQuery
                .Select(x => x.PhoneNormalized)
                .Distinct()
                .ToListAsync(ct);
            foreach (var phone in matchedPhones)
            {
                existingPhones.Add(phone);
            }
        }

        return new WorkerCandidateLookupResponse(
            existingSourceIds.ToList(),
            existingPhones.ToList());
    }
}
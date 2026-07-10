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

        var cardFingerprints = (request.CardFingerprints ?? [])
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var duplicateCutoffUtc = CandidateDuplicateLookback.GetCutoffUtc(DateTime.UtcNow);

        var existingSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sourceIds.Length > 0)
        {
            var matched = await db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.OfficeId == worker.OfficeId
                            && x.AccountId == request.AccountId
                            && x.CreatedAt >= duplicateCutoffUtc
                            && sourceIds.Contains(x.SourceResponseId))
                .Select(x => x.SourceResponseId)
                .ToListAsync(ct);
            foreach (var id in matched)
            {
                existingSourceIds.Add(id);
            }
        }

        var existingPhones = new HashSet<string>(StringComparer.Ordinal);

        if (phones.Length > 0 || request.IncludeAllKnownPhones)
        {
            var phoneQuery = db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.OfficeId == worker.OfficeId
                            && x.CreatedAt >= duplicateCutoffUtc
                            && x.PhoneNormalized != "");

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

        var existingCardFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cardFingerprints.Length > 0)
        {
            var candidateSet = cardFingerprints.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var subProfileId = request.AvitoSubProfileId?.Trim();

            var storedQuery = db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.OfficeId == worker.OfficeId
                            && x.AccountId == request.AccountId
                            && x.CreatedAt >= duplicateCutoffUtc
                            && x.CardFingerprint != "");
            if (!string.IsNullOrWhiteSpace(subProfileId))
            {
                storedQuery = storedQuery.Where(x => x.AvitoSubProfileId == subProfileId);
            }

            var storedMatches = await storedQuery
                .Where(x => candidateSet.Contains(x.CardFingerprint))
                .Select(x => x.CardFingerprint)
                .Distinct()
                .ToListAsync(ct);
            foreach (var fingerprint in storedMatches)
            {
                existingCardFingerprints.Add(fingerprint);
            }

            var remaining = candidateSet
                .Where(x => !existingCardFingerprints.Contains(x))
                .ToArray();
            if (remaining.Length > 0)
            {
                var legacyQuery = db.CandidateResponses
                    .AsNoTracking()
                    .Where(x => x.OfficeId == worker.OfficeId
                                && x.AccountId == request.AccountId
                                && x.CreatedAt >= duplicateCutoffUtc
                                && x.CardFingerprint == "");
                if (!string.IsNullOrWhiteSpace(subProfileId))
                {
                    legacyQuery = legacyQuery.Where(x => x.AvitoSubProfileId == subProfileId);
                }

                var legacyRows = await legacyQuery
                    .Select(x => new
                    {
                        x.FullName,
                        x.Vacancy,
                        x.City,
                        x.VacancyUrl,
                        x.SourceUrl,
                        x.MessengerUrl,
                        x.Age
                    })
                    .ToListAsync(ct);

                var remainingSet = remaining.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var row in legacyRows)
                {
                    var vacancyUrl = string.IsNullOrWhiteSpace(row.VacancyUrl) ? row.SourceUrl : row.VacancyUrl;
                    var fingerprint = AvitoResponseCardFingerprint.Build(
                        row.FullName,
                        row.Vacancy,
                        row.City,
                        vacancyUrl,
                        row.MessengerUrl,
                        AvitoResponseCardFingerprint.NormalizeAgeText(null, row.Age));
                    if (remainingSet.Contains(fingerprint))
                    {
                        existingCardFingerprints.Add(fingerprint);
                    }
                }
            }
        }

        return new WorkerCandidateLookupResponse(
            existingSourceIds.ToList(),
            existingPhones.ToList(),
            existingCardFingerprints.ToList());
    }
}
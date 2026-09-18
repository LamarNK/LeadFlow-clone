using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CandidateLookupService(
    OrbitaDbContext db,
    CandidatePersonMatchService personMatch,
    CandidatePhoneWatchService phoneWatches)
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
        var globalLookup = string.Equals(
            request.DuplicateScope,
            "GlobalAcrossAllAccounts",
            StringComparison.OrdinalIgnoreCase);

        var existingSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<WorkerKnownSourceResponseDto> existingSourceResponses = [];
        if (sourceIds.Length > 0)
        {
            var watchMatches = await phoneWatches.FindBySourceIdsAsync(
                request.AccountId,
                sourceIds,
                ct);
            var matched = await db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.AccountId == request.AccountId
                            && x.CreatedAt >= duplicateCutoffUtc
                            && sourceIds.Contains(x.SourceResponseId))
                .OrderByDescending(x => x.CollectedAt)
                .Select(x => new WorkerKnownSourceResponseDto(
                    x.SourceResponseId,
                    x.CollectedAt,
                    x.PhoneRaw,
                    x.PhoneNormalized))
                .ToListAsync(ct);
            var combined = watchMatches
                .Concat(matched)
                .GroupBy(x => x.SourceResponseId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(x => x.CollectedAt)
                .ToList();
            foreach (var response in combined)
            {
                existingSourceIds.Add(response.SourceResponseId);
            }

            if (request.IncludeSourceResponseMetadata)
            {
                existingSourceResponses = combined;
            }
        }

        var existingPhones = new HashSet<string>(StringComparer.Ordinal);

        if (phones.Length > 0 || request.IncludeAllKnownPhones)
        {
            var phoneQuery = db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.CreatedAt >= duplicateCutoffUtc
                            && x.PhoneNormalized != "");
            if (!globalLookup)
            {
                phoneQuery = phoneQuery.Where(x => x.OfficeId == worker.OfficeId);
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

        var existingCardFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cardFingerprints.Length > 0)
        {
            var candidateSet = cardFingerprints.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var subProfileId = request.AvitoSubProfileId?.Trim();

            var storedQuery = db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.AccountId == request.AccountId
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
                    .Where(x => x.AccountId == request.AccountId
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

        IReadOnlyList<WorkerOpenPhoneWatchDto> openPhoneWatches = [];
        var subProfileIdForWatches = request.AvitoSubProfileId?.Trim() ?? string.Empty;
        if (request.OpenPhoneWatchHours > 0)
        {
            var storedWatches = await phoneWatches.GetOpenAsync(
                request.AccountId,
                subProfileIdForWatches,
                ct);
            var watchCutoffUtc = DateTime.UtcNow.AddHours(-request.OpenPhoneWatchHours);
            var legacyWatches = await db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.OfficeId == worker.OfficeId
                            && x.AccountId == request.AccountId
                            && x.AvitoSubProfileId == subProfileIdForWatches
                            && x.SourceResponseId.StartsWith("phone-watch:")
                            && x.CollectedAt >= watchCutoffUtc
                            // Сделка закрыта в CRM любого офиса — за откликом больше не следим.
                            && !db.CrmCandidateCards.Any(card => card.ResponseId == x.Id && card.IsClosed))
                .OrderByDescending(x => x.CollectedAt)
                .Select(x => new WorkerOpenPhoneWatchDto(
                    x.SourceResponseId,
                    x.FullName,
                    x.CollectedAt,
                    x.PhoneRaw,
                    x.PhoneNormalized))
                .ToListAsync(ct);
            openPhoneWatches = storedWatches
                .Concat(legacyWatches)
                .GroupBy(x => x.SourceResponseId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(x => x.CollectedAt)
                .ToList();
        }

        var matchedProfileIndexes = new List<int>();
        var profiles = request.Profiles ?? [];
        for (var i = 0; i < profiles.Count; i++)
        {
            var item = profiles[i];
            var profile = new CandidateMatchProfile(
                item.FullName,
                item.Age,
                item.City,
                item.PhoneNormalized,
                item.ResponseAtUtc);
            var matchedPerson = await personMatch.FindMatchingPersonAsync(
                globalLookup ? null : worker.OfficeId,
                profile,
                ct);
            if (matchedPerson is not null)
            {
                matchedProfileIndexes.Add(i);
            }
        }

        return new WorkerCandidateLookupResponse(
            existingSourceIds.ToList(),
            existingPhones.ToList(),
            existingCardFingerprints.ToList(),
            matchedProfileIndexes,
            existingSourceResponses,
            openPhoneWatches);
    }
}

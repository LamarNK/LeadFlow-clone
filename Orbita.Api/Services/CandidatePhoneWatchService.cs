using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CandidatePhoneWatchService(OrbitaDbContext db)
{
    internal Task<CandidatePhoneWatchEntity?> FindExactAsync(
        Guid accountId,
        string avitoSubProfileId,
        string fullName,
        CancellationToken ct)
    {
        var subProfileId = (avitoSubProfileId ?? string.Empty).Trim();
        var fullNameKey = CandidateNameNormalizer.Normalize(fullName).FullName;
        return db.CandidatePhoneWatches
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.AccountId == accountId
                    && x.AvitoSubProfileId == subProfileId
                    && x.FullNameKey == fullNameKey,
                ct);
    }

    public async Task<IReadOnlyList<WorkerKnownSourceResponseDto>> FindBySourceIdsAsync(
        Guid accountId,
        IReadOnlyCollection<string> sourceResponseIds,
        CancellationToken ct)
    {
        if (sourceResponseIds.Count == 0)
        {
            return [];
        }

        return await db.CandidatePhoneWatches
            .AsNoTracking()
            .Where(x => x.AccountId == accountId
                && sourceResponseIds.Contains(x.PublishedSourceResponseId))
            .OrderByDescending(x => x.WatchStartedUtc)
            .Select(x => new WorkerKnownSourceResponseDto(
                x.PublishedSourceResponseId,
                x.WatchStartedUtc,
                x.CurrentPhoneRaw,
                x.CurrentPhoneNormalized,
                x.ProfileFingerprint,
                x.ChatFingerprint,
                x.State == CandidatePhoneWatchStates.ClosedInCrm))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WorkerOpenPhoneWatchDto>> GetOpenAsync(
        Guid accountId,
        string avitoSubProfileId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var subProfileId = (avitoSubProfileId ?? string.Empty).Trim();
        return await db.CandidatePhoneWatches
            .AsNoTracking()
            .Where(x => x.AccountId == accountId
                && x.AvitoSubProfileId == subProfileId
                && x.ExpiresAtUtc > now
                && (x.State == CandidatePhoneWatchStates.Open
                    || x.State == CandidatePhoneWatchStates.Changed))
            .OrderByDescending(x => x.WatchStartedUtc)
            .Select(x => new WorkerOpenPhoneWatchDto(
                x.PublishedSourceResponseId,
                x.FullName,
                x.WatchStartedUtc,
                x.CurrentPhoneRaw,
                x.CurrentPhoneNormalized,
                x.ProfileFingerprint,
                x.ChatFingerprint))
            .ToListAsync(ct);
    }

    public async Task<CandidatePhoneWatchEntity> UpsertAsync(
        WorkerEntity worker,
        WorkerCandidateDto candidate,
        Guid personId,
        Guid canonicalResponseId,
        string phoneNormalized,
        string operationKind,
        CancellationToken ct)
    {
        var now = candidate.CollectedAt == default ? DateTime.UtcNow : candidate.CollectedAt;
        var fullNameKey = CandidateNameNormalizer.Normalize(candidate.FullName).FullName;
        var subProfileId = (candidate.AvitoSubProfileId ?? string.Empty).Trim();
        var watch = await db.CandidatePhoneWatches
            .FirstOrDefaultAsync(
                x => x.AccountId == candidate.AccountId
                    && x.AvitoSubProfileId == subProfileId
                    && x.FullNameKey == fullNameKey,
                ct);

        if (watch is null)
        {
            var watchHours = ResponsePhoneWatchRules.ResolveUnchangedHours(worker.PhoneUnchangedHours);
            var startedAt = candidate.CollectedAt == default ? now : candidate.CollectedAt;
            watch = new CandidatePhoneWatchEntity
            {
                Id = Guid.NewGuid(),
                AccountId = candidate.AccountId,
                AvitoSubProfileId = subProfileId,
                FullNameKey = fullNameKey,
                WatchStartedUtc = startedAt,
                ExpiresAtUtc = watchHours <= 0 ? startedAt : startedAt.AddHours(watchHours),
                PhoneFirstSeenUtc = startedAt,
                CreatedAtUtc = now
            };
            db.CandidatePhoneWatches.Add(watch);
        }

        watch.WorkerId = worker.Id;
        watch.PersonId = personId;
        watch.CanonicalResponseId = canonicalResponseId;
        watch.FullName = candidate.FullName?.Trim() ?? string.Empty;
        watch.PublishedSourceResponseId = BuildPublishedSourceResponseId(subProfileId, fullNameKey);
        watch.CurrentPhoneRaw = candidate.PhoneRaw?.Trim() ?? string.Empty;
        watch.CurrentPhoneNormalized = phoneNormalized;
        watch.LastPublishedPhoneNormalized = phoneNormalized;
        watch.LastSeenUtc = now;
        watch.MessengerUrl = candidate.MessengerUrl?.Trim() ?? watch.MessengerUrl;

        var chatFingerprint = CandidateWatchFingerprint.Chat(candidate.ChatMessagesJson);
        if (!string.IsNullOrWhiteSpace(chatFingerprint)
            && !string.Equals(chatFingerprint, watch.ChatFingerprint, StringComparison.Ordinal))
        {
            watch.ChatMessagesJson = candidate.ChatMessagesJson;
            watch.ChatFingerprint = chatFingerprint;
        }

        watch.ProfileFingerprint = CandidateWatchFingerprint.Profile(
            candidate.City,
            candidate.Vacancy,
            candidate.Age,
            candidate.Gender,
            candidate.VacancyUrl,
            candidate.Citizenship,
            candidate.MessengerUrl);
        // Закрытый в CRM watch не возрождаем: опоздавшие публикации воркера не открывают окно заново.
        if (watch.State != CandidatePhoneWatchStates.ClosedInCrm)
        {
            watch.State = watch.ExpiresAtUtc <= now
                ? CandidatePhoneWatchStates.Expired
                : operationKind == WorkerCandidateOperationKinds.PhoneChanged
                    ? CandidatePhoneWatchStates.Changed
                    : CandidatePhoneWatchStates.Open;
        }

        watch.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return watch;
    }

    /// <summary>
    /// Закрытие сделки в CRM офиса останавливает все активные phone-watch персоны кандидата,
    /// даже если окно наблюдения ещё не истекло. Не вызывает SaveChanges — его делает вызывающий.
    /// </summary>
    public async Task<int> CloseForCardsAsync(
        IReadOnlyCollection<CrmCandidateCardEntity> cards,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var responseIds = cards
            .Select(static card => card.ResponseId)
            .Distinct()
            .ToArray();
        if (responseIds.Length == 0)
        {
            return 0;
        }

        var personIds = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => responseIds.Contains(x.Id))
            .Select(x => x.PersonId)
            .Distinct()
            .ToListAsync(ct);
        if (personIds.Count == 0)
        {
            return 0;
        }

        var activeWatches = await db.CandidatePhoneWatches
            .Where(x => personIds.Contains(x.PersonId)
                && (x.State == CandidatePhoneWatchStates.Open
                    || x.State == CandidatePhoneWatchStates.Changed))
            .ToListAsync(ct);
        foreach (var watch in activeWatches)
        {
            watch.State = CandidatePhoneWatchStates.ClosedInCrm;
            watch.ExpiresAtUtc = nowUtc;
            watch.UpdatedAtUtc = nowUtc;
        }

        return activeWatches.Count;
    }

    private static string BuildPublishedSourceResponseId(string subProfileId, string fullNameKey)
    {
        var payload = string.Join('\u001f', subProfileId, fullNameKey);
        uint hash = 2166136261;
        foreach (var ch in payload)
        {
            hash ^= ch;
            hash *= 16777619;
        }

        return $"phone-watch:{hash:x8}";
    }
}

using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

/// <summary>
/// Dedup against Orbita API with ephemeral local cache fallback.
/// </summary>
public sealed class OrbitaCandidateDuplicateRepository(
    OrbitaApiClient apiClient,
    EphemeralDedupCache dedupCache) : ICandidateDuplicateRepository
{
    public async Task<CandidateResponse?> FindDuplicateAsync(
        string phoneNormalized,
        DuplicateScope scope,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phoneNormalized))
        {
            return null;
        }

        var (_, phones) = await LookupMergedAsync(
                accountId,
                [],
                [phoneNormalized],
                scope,
                cancellationToken)
            .ConfigureAwait(false);
        return phones.Contains(phoneNormalized)
            ? new CandidateResponse { PhoneNormalized = phoneNormalized, AccountId = accountId }
            : null;
    }

    public async Task<HashSet<string>> GetExistingNormalizedPhonesAsync(
        IEnumerable<string> phoneNormalizedCandidates,
        DuplicateScope scope,
        Guid accountId,
        CancellationToken cancellationToken,
        string? avitoSubProfileId = null)
    {
        var phones = phoneNormalizedCandidates
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (phones.Length == 0)
        {
            return [];
        }

        var (_, existing) = await LookupMergedAsync(
                accountId,
                [],
                phones,
                scope,
                cancellationToken,
                avitoSubProfileId)
            .ConfigureAwait(false);
        return existing;
    }

    public async Task<HashSet<string>> GetExistingSourceResponseIdsAsync(
        Guid accountId,
        IEnumerable<string> sourceResponseIds,
        CancellationToken cancellationToken)
    {
        var ids = sourceResponseIds
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var (existing, _) = await LookupMergedAsync(
                accountId,
                ids,
                [],
                DuplicateScope.PerAvitoAccount,
                cancellationToken)
            .ConfigureAwait(false);
        return existing;
    }

    public async Task<IReadOnlyList<WorkerKnownSourceResponseDto>> GetExistingSourceResponsesAsync(
        Guid accountId,
        IEnumerable<string> sourceResponseIds,
        CancellationToken cancellationToken)
    {
        var ids = sourceResponseIds
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        try
        {
            var result = await apiClient.LookupCandidatesAsync(
                    new WorkerCandidateLookupRequest(
                        accountId,
                        DuplicateScope.PerAvitoAccount.ToString(),
                        ids,
                        [],
                        IncludeSourceResponseMetadata: true),
                    cancellationToken)
                .ConfigureAwait(false);
            return (result?.ExistingSourceResponses ?? [])
                .OrderByDescending(static x => x.CollectedAt)
                .ToArray();
        }
        catch
        {
            // Метаданные phone-watch есть только у Orbita; локальный dedup-кеш не заменяет их.
            return [];
        }
    }

    public async Task<IReadOnlyList<WorkerOpenPhoneWatchDto>> GetOpenPhoneWatchesAsync(
        Guid accountId,
        string avitoSubProfileId,
        int phoneWatchHours,
        CancellationToken cancellationToken)
    {
        if (phoneWatchHours <= 0)
        {
            return [];
        }

        try
        {
            var result = await apiClient.LookupCandidatesAsync(
                    new WorkerCandidateLookupRequest(
                        accountId,
                        DuplicateScope.PerAvitoAccount.ToString(),
                        [],
                        [],
                        AvitoSubProfileId: (avitoSubProfileId ?? string.Empty).Trim(),
                        OpenPhoneWatchHours: phoneWatchHours),
                    cancellationToken)
                .ConfigureAwait(false);
            return result?.OpenPhoneWatches ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<HashSet<string>> GetExistingCardFingerprintsAsync(
        IEnumerable<string> cardFingerprintCandidates,
        DuplicateScope scope,
        Guid accountId,
        CancellationToken cancellationToken,
        string? avitoSubProfileId = null)
    {
        var fingerprints = cardFingerprintCandidates
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (fingerprints.Length == 0)
        {
            return [];
        }

        WorkerCandidateLookupResponse? apiResult = null;
        try
        {
            apiResult = await apiClient.LookupCandidatesAsync(
                    new WorkerCandidateLookupRequest(
                        accountId,
                        scope.ToString(),
                        [],
                        [],
                        AvitoSubProfileId: avitoSubProfileId,
                        CardFingerprints: fingerprints),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Fingerprints are API-only; EphemeralDedupCache does not store them.
        }

        var existing = apiResult?.ExistingCardFingerprints
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];
        return existing;
    }

    public async Task<HashSet<int>> GetMatchedProfileIndicesAsync(
        IReadOnlyList<CandidateLookupProfileDto> profiles,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (profiles.Count == 0)
        {
            return [];
        }

        var phones = profiles
            .Select(static p => p.PhoneNormalized)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        WorkerCandidateLookupResponse? apiResult = null;
        var apiReached = false;
        try
        {
            apiResult = await apiClient.LookupCandidatesAsync(
                    new WorkerCandidateLookupRequest(
                        accountId,
                        DuplicateScope.GlobalAcrossAllAccounts.ToString(),
                        [],
                        phones,
                        Profiles: profiles),
                    cancellationToken)
                .ConfigureAwait(false);
            apiReached = apiResult is not null;
        }
        catch
        {
            // fallback to cache phones below
        }

        var matched = await CandidateProfileDuplicateMatchResolver.ResolveAsync(
                profiles,
                apiResult,
                async () =>
                {
                    var cacheResult = await dedupCache.LookupAsync(
                            accountId,
                            [],
                            phones,
                            DuplicateScope.GlobalAcrossAllAccounts,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return cacheResult.Phones;
                })
            .ConfigureAwait(false);

        CandidateDedupLog.LogPersonProfileLookup(
            accountId,
            profiles.Count,
            apiReached,
            apiResult?.MatchedProfileIndexes.Count ?? 0,
            matched.Count);

        return matched;
    }

    public Task RecordSeenAsync(
        Guid accountId,
        string? sourceResponseId,
        string? phoneNormalized,
        string? avitoSubProfileId = null,
        CancellationToken cancellationToken = default) =>
        dedupCache.RecordAsync(
            accountId,
            sourceResponseId,
            phoneNormalized,
            DateTime.UtcNow,
            avitoSubProfileId,
            cancellationToken);

    private async Task<(HashSet<string> SourceIds, HashSet<string> Phones)> LookupMergedAsync(
        Guid accountId,
        IReadOnlyList<string> sourceResponseIds,
        IReadOnlyList<string> phoneNormalized,
        DuplicateScope scope,
        CancellationToken cancellationToken,
        string? avitoSubProfileId = null)
    {
        WorkerCandidateLookupResponse? apiResult = null;
        var apiReached = false;
        try
        {
            apiResult = await apiClient.LookupCandidatesAsync(
                    new WorkerCandidateLookupRequest(
                        accountId,
                        scope.ToString(),
                        sourceResponseIds,
                        phoneNormalized,
                        AvitoSubProfileId: avitoSubProfileId),
                    cancellationToken)
                .ConfigureAwait(false);
            apiReached = apiResult is not null;
        }
        catch
        {
            // cache only
        }

        var cacheResult = await dedupCache.LookupAsync(
                accountId,
                sourceResponseIds,
                phoneNormalized,
                scope,
                cancellationToken,
                avitoSubProfileId)
            .ConfigureAwait(false);

        var apiSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apiPhones = new HashSet<string>(StringComparer.Ordinal);
        if (apiResult is not null)
        {
            foreach (var id in apiResult.ExistingSourceResponseIds)
            {
                apiSourceIds.Add(id);
            }

            foreach (var phone in apiResult.ExistingPhones)
            {
                apiPhones.Add(phone);
            }
        }

        var sourceIds = new HashSet<string>(apiSourceIds, StringComparer.OrdinalIgnoreCase);
        var phones = new HashSet<string>(apiPhones, StringComparer.Ordinal);
        sourceIds.UnionWith(cacheResult.SourceIds);
        phones.UnionWith(cacheResult.Phones);

        CandidateDedupLog.LogOrbitaApiLookup(
            accountId,
            scope,
            avitoSubProfileId,
            sourceResponseIds.Count,
            phoneNormalized.Count,
            apiReached,
            apiSourceIds.Count,
            apiPhones.Count,
            cacheResult.SourceIds.Count,
            cacheResult.Phones.Count,
            sourceIds.Count,
            phones.Count,
            phones);

        return (sourceIds, phones);
    }
}

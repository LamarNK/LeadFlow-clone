using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
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
        CancellationToken cancellationToken)
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
                cancellationToken)
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

    public async Task<HashSet<string>> GetAllStoredNormalizedPhonesAsync(
        DuplicateScope scope,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var phones = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var apiResult = await apiClient.LookupCandidatesAsync(
                    new WorkerCandidateLookupRequest(
                        accountId,
                        scope.ToString(),
                        [],
                        [],
                        IncludeAllKnownPhones: true),
                    cancellationToken)
                .ConfigureAwait(false);
            if (apiResult is not null)
            {
                foreach (var phone in apiResult.ExistingPhones)
                {
                    phones.Add(phone);
                }
            }
        }
        catch
        {
            // fallback to cache
        }

        var cachePhones = await dedupCache.GetAllPhonesAsync(scope, accountId, cancellationToken)
            .ConfigureAwait(false);
        phones.UnionWith(cachePhones);
        return phones;
    }

    public Task RecordSeenAsync(
        Guid accountId,
        string? sourceResponseId,
        string? phoneNormalized,
        CancellationToken cancellationToken = default) =>
        dedupCache.RecordAsync(
            accountId,
            sourceResponseId,
            phoneNormalized,
            DateTime.UtcNow,
            cancellationToken);

    private async Task<(HashSet<string> SourceIds, HashSet<string> Phones)> LookupMergedAsync(
        Guid accountId,
        IReadOnlyList<string> sourceResponseIds,
        IReadOnlyList<string> phoneNormalized,
        DuplicateScope scope,
        CancellationToken cancellationToken)
    {
        WorkerCandidateLookupResponse? apiResult = null;
        try
        {
            apiResult = await apiClient.LookupCandidatesAsync(
                    new WorkerCandidateLookupRequest(
                        accountId,
                        scope.ToString(),
                        sourceResponseIds,
                        phoneNormalized),
                    cancellationToken)
                .ConfigureAwait(false);
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
                cancellationToken)
            .ConfigureAwait(false);

        var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phones = new HashSet<string>(StringComparer.Ordinal);

        if (apiResult is not null)
        {
            foreach (var id in apiResult.ExistingSourceResponseIds)
            {
                sourceIds.Add(id);
            }

            foreach (var phone in apiResult.ExistingPhones)
            {
                phones.Add(phone);
            }
        }

        sourceIds.UnionWith(cacheResult.SourceIds);
        phones.UnionWith(cacheResult.Phones);
        return (sourceIds, phones);
    }
}
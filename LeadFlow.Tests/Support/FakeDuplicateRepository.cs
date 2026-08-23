using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using Orbita.Contracts;

namespace LeadFlow.Tests.Support;

internal sealed class FakeDuplicateRepository : ICandidateDuplicateRepository
{
    public Func<string, DuplicateScope, Guid, CandidateResponse?> Lookup { get; set; }
        = (_, _, _) => null;

    public int CallCount { get; private set; }
    public string? LastPhoneArgument { get; private set; }
    public DuplicateScope? LastScopeArgument { get; private set; }
    public string? LastSubProfileArgument { get; private set; }

    public Task<CandidateResponse?> FindDuplicateAsync(
        string phoneNormalized, DuplicateScope scope, Guid accountId, CancellationToken cancellationToken)
    {
        CallCount++;
        LastPhoneArgument = phoneNormalized;
        LastScopeArgument = scope;
        return Task.FromResult(Lookup(phoneNormalized, scope, accountId));
    }

    public Task<HashSet<string>> GetExistingNormalizedPhonesAsync(
        IEnumerable<string> phoneNormalizedCandidates,
        DuplicateScope scope,
        Guid accountId,
        CancellationToken cancellationToken,
        string? avitoSubProfileId = null)
    {
        LastScopeArgument = scope;
        LastSubProfileArgument = avitoSubProfileId;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in phoneNormalizedCandidates)
        {
            if (string.IsNullOrWhiteSpace(p))
            {
                continue;
            }

            if (Lookup(p, scope, accountId) is not null)
            {
                result.Add(p);
            }
        }

        return Task.FromResult(result);
    }

    public Task<HashSet<string>> GetExistingSourceResponseIdsAsync(
        Guid accountId,
        IEnumerable<string> sourceResponseIds,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HashSet<string>(StringComparer.Ordinal));

    public IReadOnlyList<WorkerKnownSourceResponseDto> ExistingSourceResponses { get; set; } = [];

    public Task<IReadOnlyList<WorkerKnownSourceResponseDto>> GetExistingSourceResponsesAsync(
        Guid accountId,
        IEnumerable<string> sourceResponseIds,
        CancellationToken cancellationToken)
    {
        var requested = sourceResponseIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<WorkerKnownSourceResponseDto> result = ExistingSourceResponses
            .Where(x => requested.Contains(x.SourceResponseId))
            .OrderByDescending(static x => x.CollectedAt)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<HashSet<string>> GetExistingCardFingerprintsAsync(
        IEnumerable<string> cardFingerprintCandidates,
        DuplicateScope scope,
        Guid accountId,
        CancellationToken cancellationToken,
        string? avitoSubProfileId = null) =>
        Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public Func<IReadOnlyList<CandidateLookupProfileDto>, Guid, HashSet<int>> ProfileMatch { get; set; }
        = (_, _) => [];

    public Task<HashSet<int>> GetMatchedProfileIndicesAsync(
        IReadOnlyList<CandidateLookupProfileDto> profiles,
        Guid accountId,
        CancellationToken cancellationToken) =>
        Task.FromResult(ProfileMatch(profiles, accountId));
}

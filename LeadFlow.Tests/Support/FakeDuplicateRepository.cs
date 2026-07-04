using LeadFlow.Core.Data;
using LeadFlow.Core.Models;

namespace LeadFlow.Tests.Support;

internal sealed class FakeDuplicateRepository : ICandidateDuplicateRepository
{
    public Func<string, DuplicateScope, Guid, CandidateResponse?> Lookup { get; set; }
        = (_, _, _) => null;

    public int CallCount { get; private set; }
    public string? LastPhoneArgument { get; private set; }
    public DuplicateScope? LastScopeArgument { get; private set; }

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
        CancellationToken cancellationToken)
    {
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

    public Func<DuplicateScope, Guid, HashSet<string>> AllStoredPhonesLookup { get; set; }
        = (_, _) => [];

    public Task<HashSet<string>> GetAllStoredNormalizedPhonesAsync(
        DuplicateScope scope,
        Guid accountId,
        CancellationToken cancellationToken) =>
        Task.FromResult(AllStoredPhonesLookup(scope, accountId));
}

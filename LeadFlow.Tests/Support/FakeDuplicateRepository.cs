using LeadFlow.Data;
using LeadFlow.Models;

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
}

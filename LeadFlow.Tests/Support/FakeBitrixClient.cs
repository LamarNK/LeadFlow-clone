using LeadFlow.Models;
using LeadFlow.Services.Bitrix;

namespace LeadFlow.Tests.Support;

internal sealed class FakeBitrixClient : IBitrixClient
{
    public Func<string, AppSettings, BitrixDuplicateLookupResult> HasDuplicateImpl { get; set; }
        = (_, _) => new BitrixDuplicateLookupResult(BitrixDuplicateLookupOutcome.NoDuplicate);

    public Func<CandidateResponse, AppSettings, BitrixCreateLeadResponse> CreateLeadImpl { get; set; }
        = (_, _) => new BitrixCreateLeadResponse { IsSuccess = true, EntityId = "DEMO" };

    public Func<CandidateResponse, string, AppSettings, BitrixCreateLeadResponse> CreateDealForContactImpl { get; set; }
        = (_, contactId, _) => new BitrixCreateLeadResponse { IsSuccess = true, EntityId = "DEMO", ContactId = contactId };

    public Func<AppSettings, IReadOnlyList<CandidateResponse>> GetExistingLeadsImpl { get; set; }
        = _ => Array.Empty<CandidateResponse>();

    public int HasDuplicateCallCount { get; private set; }
    public int CreateLeadCallCount { get; private set; }

    public Task<BitrixDuplicateLookupResult> HasDuplicateAsync(
        string phoneNormalized, AppSettings settings, CancellationToken cancellationToken)
    {
        HasDuplicateCallCount++;
        return Task.FromResult(HasDuplicateImpl(phoneNormalized, settings));
    }

    public Task<IReadOnlyList<CandidateResponse>> GetExistingLeadsAsync(AppSettings settings, CancellationToken cancellationToken)
        => Task.FromResult(GetExistingLeadsImpl(settings));

    public Task<BitrixCreateLeadResponse> CreateLeadAsync(CandidateResponse response, AppSettings settings, CancellationToken cancellationToken)
    {
        CreateLeadCallCount++;
        return Task.FromResult(CreateLeadImpl(response, settings));
    }

    public Task<BitrixCreateLeadResponse> CreateDealForContactAsync(
        CandidateResponse response, string contactId, AppSettings settings, CancellationToken cancellationToken)
        => Task.FromResult(CreateDealForContactImpl(response, contactId, settings));
}

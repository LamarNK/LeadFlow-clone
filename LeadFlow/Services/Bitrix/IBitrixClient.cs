using LeadFlow.Models;

namespace LeadFlow.Services.Bitrix;

public interface IBitrixClient
{
    Task<bool> HasDuplicateAsync(string phoneNormalized, AppSettings settings, CancellationToken cancellationToken);
    Task<BitrixCreateLeadResponse> CreateLeadAsync(CandidateResponse response, AppSettings settings, CancellationToken cancellationToken);
}

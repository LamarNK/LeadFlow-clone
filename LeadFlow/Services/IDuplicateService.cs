using LeadFlow.Models;

namespace LeadFlow.Services;

public interface IDuplicateService
{
    Task<DuplicateCheckResult> CheckAsync(CandidateResponse response, AppSettings settings, CancellationToken cancellationToken);
}

using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services;

public interface IDuplicateService
{
    Task<DuplicateCheckResult> CheckAsync(CandidateResponse response, AppSettings settings, CancellationToken cancellationToken);
}

using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Worker;

public interface INewCandidateSink
{
    Task<CandidatePublishResult> PublishAsync(CandidateResponse candidate, CancellationToken cancellationToken);
}
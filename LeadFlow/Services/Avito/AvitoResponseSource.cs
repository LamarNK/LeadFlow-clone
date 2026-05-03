using LeadFlow.Models;

namespace LeadFlow.Services.Avito;

public sealed class AvitoResponseSource : IAvitoResponseSource
{
    public Task<IReadOnlyList<CandidateResponse>> GetNewResponsesAsync(AvitoAccount account, AppSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CandidateResponse>>([]);
}

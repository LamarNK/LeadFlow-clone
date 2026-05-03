using LeadFlow.Models;

namespace LeadFlow.Services.Avito;

public interface IAvitoResponseSource
{
    Task<IReadOnlyList<CandidateResponse>> GetNewResponsesAsync(AvitoAccount account, AppSettings settings, CancellationToken cancellationToken);
}

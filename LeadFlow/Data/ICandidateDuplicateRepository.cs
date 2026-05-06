using LeadFlow.Models;

namespace LeadFlow.Data;

/// <summary>Узкая абстракция над репозиторием для проверки локальных дублей в DuplicateService.</summary>
public interface ICandidateDuplicateRepository
{
    Task<CandidateResponse?> FindDuplicateAsync(
        string phoneNormalized,
        DuplicateScope scope,
        Guid accountId,
        CancellationToken cancellationToken);
}

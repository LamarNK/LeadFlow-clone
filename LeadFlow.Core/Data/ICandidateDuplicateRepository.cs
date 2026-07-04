using LeadFlow.Core.Models;

namespace LeadFlow.Core.Data;

/// <summary>Узкая абстракция над репозиторием для проверки локальных дублей в DuplicateService.</summary>
public interface ICandidateDuplicateRepository
{
    Task<CandidateResponse?> FindDuplicateAsync(
        string phoneNormalized,
        DuplicateScope scope,
        Guid accountId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Batch lookup: какие из переданных нормализованных номеров уже есть в базе
    /// (пропуск кликов detail-enrich и messenger-enrich на странице откликов).
    /// </summary>
    Task<HashSet<string>> GetExistingNormalizedPhonesAsync(
        IEnumerable<string> phoneNormalizedCandidates,
        DuplicateScope scope,
        Guid accountId,
        CancellationToken cancellationToken,
        string? avitoSubProfileId = null);

    Task<HashSet<string>> GetExistingSourceResponseIdsAsync(
        Guid accountId,
        IEnumerable<string> sourceResponseIds,
        CancellationToken cancellationToken);
}

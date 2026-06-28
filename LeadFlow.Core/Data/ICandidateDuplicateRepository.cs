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
    /// Какие из переданных нормализованных номеров уже встречались в сохранённых откликах
    /// (для пропуска тяжёлого обогащения мессенджером на странице кандидатов).
    /// </summary>
    Task<HashSet<string>> GetExistingNormalizedPhonesAsync(
        IEnumerable<string> phoneNormalizedCandidates,
        DuplicateScope scope,
        Guid accountId,
        CancellationToken cancellationToken);

    Task<HashSet<string>> GetExistingSourceResponseIdsAsync(
        IEnumerable<string> sourceResponseIds,
        CancellationToken cancellationToken);
}

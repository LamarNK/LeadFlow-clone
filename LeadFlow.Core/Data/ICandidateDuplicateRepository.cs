using LeadFlow.Core.Models;
using Orbita.Contracts;

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

    /// <summary>
    /// Какие из ключей карточек Avito (без телефона) уже есть в локальной базе для аккаунта / суб-профиля.
    /// </summary>
    Task<HashSet<string>> GetExistingCardFingerprintsAsync(
        IEnumerable<string> cardFingerprintCandidates,
        DuplicateScope scope,
        Guid accountId,
        CancellationToken cancellationToken,
        string? avitoSubProfileId = null);

    /// <summary>
    /// Batch lookup: индексы профилей, для которых в офисе уже есть кандидат (score &gt;= 70).
    /// </summary>
    Task<HashSet<int>> GetMatchedProfileIndicesAsync(
        IReadOnlyList<CandidateLookupProfileDto> profiles,
        Guid accountId,
        CancellationToken cancellationToken);
}

namespace Orbita.Contracts;

public sealed record CandidateMatchProfile(
    string FullName,
    int? Age,
    string City,
    string PhoneNormalized,
    /// <summary>Дата отклика (из чата Avito). Нужна для неполного имени (окно ~неделя).</summary>
    DateTime? ResponseAtUtc = null);
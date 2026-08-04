namespace Orbita.Api.Models;

public sealed class CandidateLead
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string Source { get; set; } = "Avito";
    public string SourceResponseId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string MiddleName { get; set; } = string.Empty;
    public int? Age { get; set; }
    public string PhoneRaw { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Vacancy { get; set; } = string.Empty;
    public string VacancyUrl { get; set; } = string.Empty;
    public string MessengerUrl { get; set; } = string.Empty;
    public string AvitoSubProfileId { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>История номеров (хронологически) для COMMENTS в Bitrix.</summary>
    public IReadOnlyList<CandidateLeadPhoneHistoryItem> PhoneHistory { get; set; } = [];
}

public sealed record CandidateLeadPhoneHistoryItem(
    string PhoneRaw,
    string PhoneNormalized,
    DateTime RecordedAtUtc);
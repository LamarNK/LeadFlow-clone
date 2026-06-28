namespace LeadFlow.Core.Data;

public sealed class CandidateResponseEntity
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
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
    // Legacy column retained for backward-compatible inserts into older SQLite schemas.
    public string SourceUrl { get; set; } = string.Empty;
    public string VacancyUrl { get; set; } = string.Empty;
    public string MessengerUrl { get; set; } = string.Empty;
    public string AvitoSubProfileId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string BitrixEntityType { get; set; } = string.Empty;
    public string BitrixEntityId { get; set; } = string.Empty;
    public string BitrixContactId { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

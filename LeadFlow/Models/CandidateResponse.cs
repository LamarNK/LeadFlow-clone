namespace LeadFlow.Models;

public sealed class CandidateResponse
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
    /// <summary>URL объявления вакансии на Avito, на которую откликнулся кандидат (href у job-application/link/to-resume — строка «вакансия · город», не ссылка «Резюме» / cv-button).</summary>
    public string VacancyUrl { get; set; } = string.Empty;
    /// <summary>Исторически дублирует вакансию; для отображения и внешних ссылок используйте <see cref="EffectiveVacancyUrl"/>.</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>Актуальная ссылка на вакансию или запись из SourceUrl у старых откликов.</summary>
    public string EffectiveVacancyUrl =>
        !string.IsNullOrWhiteSpace(VacancyUrl) ? VacancyUrl : SourceUrl;
    /// <summary>Ссылка на чат с кандидатом в мессенджере Avito (если удалось извлечь при парсинге).</summary>
    public string MessengerUrl { get; set; } = string.Empty;
    public ResponseStatus Status { get; set; } = ResponseStatus.New;
    public string BitrixEntityType { get; set; } = "Deal";
    public string BitrixEntityId { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}

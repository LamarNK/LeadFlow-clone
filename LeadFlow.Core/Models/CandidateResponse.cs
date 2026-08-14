namespace LeadFlow.Core.Models;

public sealed class CandidateResponse
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string Source { get; set; } = "Avito";
    public string SourceResponseId { get; set; } = string.Empty;

    /// <summary>Ключ карточки Avito без телефона (имя, вакансия, чат и т.д.) — для пропуска раскрытия номера.</summary>
    public string CardFingerprint { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string MiddleName { get; set; } = string.Empty;
    public int? Age { get; set; }
    public string Gender { get; set; } = string.Empty;
    public string Citizenship { get; set; } = string.Empty;
    public string PhoneRaw { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Vacancy { get; set; } = string.Empty;
    /// <summary>URL объявления вакансии на Avito, на которую откликнулся кандидат (href у job-application/link/to-resume — строка «вакансия · город», не ссылка «Резюме» / cv-button).</summary>
    public string VacancyUrl { get; set; } = string.Empty;
    /// <summary>Ссылка на чат с кандидатом в мессенджере Avito (если удалось извлечь при парсинге).</summary>
    public string MessengerUrl { get; set; } = string.Empty;
    /// <summary>
    /// URL аватара, извлечённый из DOM. Это только промежуточное поле воркера:
    /// в Orbita передаются скачанные байты, а не ссылка на CDN Avito.
    /// </summary>
    public string AvatarUrl { get; set; } = string.Empty;

    /// <summary>JSON-массив сообщений мини-чата Avito (<see cref="AvitoChatMessage"/>).</summary>
    public string ChatMessagesJson { get; set; } = string.Empty;

    /// <summary>
    /// Суб-профиль Avito Pro (<c>data-marker=component-profile-switch/profile-…</c>), с которого собран отклик в AdsPower.
    /// Пусто — один кабинет в профиле или встроенный WebView2.
    /// </summary>
    public string AvitoSubProfileId { get; set; } = string.Empty;

    /// <summary>Имя субпрофиля Avito Pro на момент сбора отклика (не зависит от текущего списка аккаунтов воркера).</summary>
    public string AvitoSubProfileName { get; set; } = string.Empty;

    public ResponseStatus Status { get; set; } = ResponseStatus.New;
    public string BitrixEntityType { get; set; } = "Deal";
    public string BitrixEntityId { get; set; } = string.Empty;

    /// <summary>ID контакта в Bitrix24 (после успешной отправки или при сбое создания сделки, если контакт остался в портале).</summary>
    public string BitrixContactId { get; set; } = string.Empty;

    public string ErrorMessage { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    /// <summary>Дата отклика на Avito (из чата); при отсутствии — совпадает со сбором.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Момент сбора отклика воркером.</summary>
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }

    /// <summary>Метрика номера: <see cref="Orbita.Contracts.ResponsePhoneMetricKinds"/>.</summary>
    public string PhoneMetricKind { get; set; } = string.Empty;
    public string? PreviousPhoneRaw { get; set; }
    public string? PreviousPhoneNormalized { get; set; }
    public int? PhoneUnchangedHours { get; set; }
    public DateTime? PhoneChangedAtUtc { get; set; }
}

namespace LeadFlow.Models;

/// <summary>
/// Один из суб-профилей внутри Avito Pro мульти-аккаунта (см. модалку
/// <c>https://www.avito.ru/profile/dashboard#profile/switch?withEntities=true</c>).
/// </summary>
public sealed class AvitoSubProfile
{
    /// <summary>Идентификатор Avito (data-marker="component-profile-switch/profile-{Id}").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Имя профиля в модалке (например, «Служба России 3»).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Категория профиля (например, «Работа»).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>True, если профиль сейчас выбран (ProfileCard-module-isCurrent).</summary>
    public bool IsCurrent { get; set; }

    /// <summary>Баланс на Avito для этого субпрофиля (null — не указан, 0 — пустой).</summary>
    public decimal? Balance { get; set; }

    /// <summary>Тип последней проблемы (<see cref="AvitoSubProfileIssueKind"/>); пусто — всё в порядке.</summary>
    public string LastIssueKind { get; set; } = string.Empty;

    /// <summary>Краткое описание последней проблемы для UI.</summary>
    public string LastIssueMessage { get; set; } = string.Empty;

    /// <summary>Когда зафиксирована последняя проблема (UTC).</summary>
    public DateTime? LastIssueAt { get; set; }

    public bool HasIssue => !string.IsNullOrWhiteSpace(LastIssueKind);

    public string IssueKindLabel => AvitoSubProfileIssueKind.ToDisplayLabel(LastIssueKind);

    public string IssueSummaryLine => HasIssue
        ? $"«{DisplayName}» — {IssueKindLabel}: {LastIssueMessage}"
        : string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

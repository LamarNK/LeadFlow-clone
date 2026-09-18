namespace LeadFlow.Core.Models;

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

    /// <summary>Баланс «Аванс» на Avito для этого субпрофиля (null — не указан, 0 — пустой).</summary>
    public decimal? Balance { get; set; }

    /// <summary>Баланс «Кошелёк» на Avito для этого субпрофиля.</summary>
    public decimal? WalletBalance { get; set; }

    /// <summary>Оценка Avito, на сколько хватит аванса (например, «~ на 9 дней»).</summary>
    public string AdvanceDurationText { get; set; } = string.Empty;

    /// <summary>Рейтинг субпрофиля из сайдбара (<c>osp-sidebar/tools/stats/rating</c>).</summary>
    public decimal? Rating { get; set; }

    /// <summary>Число отзывов, если удалось извлечь из текста («1 отзыв», «5 отзывов»).</summary>
    public int? ReviewsCount { get; set; }

    /// <summary>Сырой текст отзывов из сайдбара (например, «1 отзыв»).</summary>
    public string ReviewsText { get; set; } = string.Empty;

    /// <summary>Тип последней проблемы (<see cref="AvitoSubProfileIssueKind"/>); пусто — всё в порядке.</summary>
    public string LastIssueKind { get; set; } = string.Empty;

    /// <summary>Краткое описание последней проблемы для UI.</summary>
    public string LastIssueMessage { get; set; } = string.Empty;

    /// <summary>Когда зафиксирована последняя проблема (UTC).</summary>
    public DateTime? LastIssueAt { get; set; }

    /// <summary>Последний диагностический скриншот страницы при ошибке субпрофиля.</summary>
    public Guid? LastDiagnosticAttachmentId { get; set; }

    /// <summary>
    /// Сколько подряд проходов субпрофиль завершался с проблемой (эскалация повторяющихся
    /// сбоев: разовые транзиенты не считаются — счётчик сбрасывается первым же успехом).
    /// </summary>
    public int ConsecutivePassFailures { get; set; }

    /// <summary>Проход (MonitoringPassStartedAtUtc), в котором сбой уже учтён — не считаем дважды при отложенном повторе.</summary>
    public DateTime? LastFailurePassStartedAtUtc { get; set; }

    public bool HasIssue => !string.IsNullOrWhiteSpace(LastIssueKind);

    public string IssueKindLabel => AvitoSubProfileIssueKind.ToDisplayLabel(LastIssueKind);

    public string IssueSummaryLine => HasIssue
        ? $"«{DisplayName}» — {IssueKindLabel}: {LastIssueMessage}"
        : string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

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
}

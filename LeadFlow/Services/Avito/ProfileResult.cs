using LeadFlow.Models;

namespace LeadFlow.Services.Avito;

/// <summary>
/// Результат разбора страницы объявлений Avito Pro для одного прохода (вкладка активных ± rejected).
/// </summary>
public sealed class ProfileResult
{
    /// <summary>
    /// Успешно получен HTML активной вкладки и выполнен разбор (даже если вакансий в DOM не оказалось).
    /// Ложь — пустой/бракованный HTML, сбой загрузки до парсера и т.п.: снимок аккаунта обновлять нельзя.
    /// </summary>
    public bool ParseSuccess { get; set; } = true;

    public string? ParseFailureReason { get; set; }

    /// <summary>
    /// Сколько раз в сыром HTML активной вкладки встретился маркер <c>data-marker="item-snippet/{id}"</c>.
    /// Нужен для защиты от преждевременного снятия DOM (спиннер): вкладка показывает ненулевой счётчик, а карточек ещё нет.
    /// При агрегации нескольких суб-профилей суммируется.
    /// </summary>
    public int ItemSnippetMarkersFound { get; set; }

    /// <summary>
    /// HTML списка объявлений получен (не пустой) до вызова парсера — иначе «ноль вакансий» не считаем подтверждённым.
    /// </summary>
    public bool PageLoadedSuccessfully { get; set; }

    /// <summary>
    /// В разметке найден счётчик вкладки «Активные» (<c>profile-items-tab/tab(active)</c>).
    /// Если false, <see cref="ActiveCount"/> == 0 нельзя трактовать как «в кабинете 0 объявлений».
    /// При агрегации суб-профилей: логическое AND.
    /// </summary>
    public bool ActiveTabCounterResolved { get; set; }

    public int ActiveCount { get; set; }
    public int BlockedCount { get; set; }
    public int DraftsCount { get; set; }
    public List<AvitoAdStatus> ActiveAds { get; set; } = new();

    /// <summary>Заблокированные/«с ошибками» вакансии — парсятся отдельным проходом по вкладке rejected.</summary>
    public List<AvitoAdStatus> BlockedAds { get; set; } = new();

    /// <summary>Баланс «Аванс» из боковой панели, если удалось распарсить.</summary>
    public decimal? Balance { get; set; }
}

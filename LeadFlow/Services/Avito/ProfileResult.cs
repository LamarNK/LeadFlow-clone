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

    public int ActiveCount { get; set; }
    public int BlockedCount { get; set; }
    public int DraftsCount { get; set; }
    public List<AvitoAdStatus> ActiveAds { get; set; } = new();

    /// <summary>Заблокированные/«с ошибками» вакансии — парсятся отдельным проходом по вкладке rejected.</summary>
    public List<AvitoAdStatus> BlockedAds { get; set; } = new();
}

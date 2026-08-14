using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Пока окно phone-watch открыто, отклик с тем же номером всё равно можно дослать в Орбиту:
/// обновить чат и поля карточки, которые Avito уже умеет парсить.
/// </summary>
public static class ResponsePhoneWatchChatRefresh
{
    public static bool HasProfileRefresh(CandidateResponse? candidate) =>
        candidate is not null
        && (!string.IsNullOrWhiteSpace(candidate.City)
            || !string.IsNullOrWhiteSpace(candidate.Vacancy)
            || candidate.Age is > 0
            || !string.IsNullOrWhiteSpace(candidate.Gender)
            || !string.IsNullOrWhiteSpace(candidate.VacancyUrl)
            || !string.IsNullOrWhiteSpace(candidate.Citizenship)
            || !string.IsNullOrWhiteSpace(candidate.MessengerUrl));

    public static bool ShouldPublish(
        bool watchingOpen,
        ResponsePhoneWatchAction action,
        string? chatMessagesJson,
        bool hasProfileRefresh = false) =>
        watchingOpen
        && action == ResponsePhoneWatchAction.Skip
        && (!string.IsNullOrWhiteSpace(chatMessagesJson) || hasProfileRefresh);
}

using LeadFlow.Core.Models;
using Orbita.Contracts;

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

    public static bool HasPayloadChanged(
        CandidateResponse candidate,
        WorkerKnownSourceResponseDto? stored)
    {
        if (stored is null)
        {
            return HasProfileRefresh(candidate)
                || !string.IsNullOrWhiteSpace(candidate.ChatMessagesJson);
        }

        var profileFingerprint = CandidateWatchFingerprint.Profile(
            candidate.City,
            candidate.Vacancy,
            candidate.Age,
            candidate.Gender,
            candidate.VacancyUrl,
            candidate.Citizenship,
            candidate.MessengerUrl);
        var chatFingerprint = CandidateWatchFingerprint.Chat(candidate.ChatMessagesJson);
        return !string.Equals(profileFingerprint, stored.ProfileFingerprint, StringComparison.Ordinal)
            || !string.Equals(chatFingerprint, stored.ChatFingerprint, StringComparison.Ordinal);
    }

    public static bool ShouldPublish(
        bool watchingOpen,
        ResponsePhoneWatchAction action,
        string? chatMessagesJson,
        bool hasProfileRefresh = false) =>
        watchingOpen
        && action == ResponsePhoneWatchAction.Skip
        && (!string.IsNullOrWhiteSpace(chatMessagesJson) || hasProfileRefresh);
}

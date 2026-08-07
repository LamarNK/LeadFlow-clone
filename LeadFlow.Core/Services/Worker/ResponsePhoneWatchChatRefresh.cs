namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Пока окно phone-watch открыто, отклик с тем же номером всё равно можно дослать в Орбиту,
/// чтобы обновить чат (кандидат мог ответить/написать).
/// </summary>
public static class ResponsePhoneWatchChatRefresh
{
    public static bool ShouldPublish(
        bool watchingOpen,
        ResponsePhoneWatchAction action,
        string? chatMessagesJson) =>
        watchingOpen
        && action == ResponsePhoneWatchAction.Skip
        && !string.IsNullOrWhiteSpace(chatMessagesJson);
}

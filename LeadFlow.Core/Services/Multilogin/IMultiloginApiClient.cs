namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Клиент Multilogin X: launcher start/stop и подтверждённый cloud profile/search.
/// </summary>
public interface IMultiloginApiClient
{
    Task<MultiloginBrowserStartResult> StartProfileAsync(
        MultiloginConnectionOptions options,
        string folderId,
        string profileId,
        CancellationToken cancellationToken = default);

    Task StopProfileAsync(
        MultiloginConnectionOptions options,
        string profileId,
        CancellationToken cancellationToken = default);

    Task<MultiloginProfileSearchResult> SearchProfilesAsync(
        MultiloginConnectionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверяет, что launcher отвечает по сети. Не запускает профиль.
    /// Любой HTTP-ответ считается успехом; ошибка соединения — launcher недоступен.
    /// </summary>
    Task ProbeLauncherAsync(
        MultiloginConnectionOptions options,
        CancellationToken cancellationToken = default);
}

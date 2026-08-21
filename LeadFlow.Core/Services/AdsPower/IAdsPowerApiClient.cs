namespace LeadFlow.Core.Services.AdsPower;

public interface IAdsPowerApiClient
{
    Task<IReadOnlyList<AdsPowerProfileSummary>> ListProfilesAsync(
        AdsPowerConnectionOptions options,
        CancellationToken cancellationToken = default,
        string? groupId = null);

    /// <summary>
    /// Считывает proxy текущего AdsPower-профиля из Local API. Нужен решателю капчи,
    /// чтобы задача RuCaptcha шла с того же IP, что и браузер.
    /// </summary>
    Task<AdsPowerProfileProxy?> GetProfileProxyAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Список групп профилей AdsPower (GET /api/v1/group/list).
    /// </summary>
    Task<IReadOnlyList<AdsPowerGroupSummary>> ListGroupsAsync(
        AdsPowerConnectionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Запускает браузер профиля AdsPower (GET /api/v1/browser/start).
    /// </summary>
    Task<AdsPowerBrowserStartResult> StartBrowserAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        string? openUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Закрывает браузер профиля AdsPower (GET /api/v1/browser/stop).
    /// </summary>
    Task StopBrowserAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default);
}

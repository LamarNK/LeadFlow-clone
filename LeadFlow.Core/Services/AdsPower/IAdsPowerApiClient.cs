namespace LeadFlow.Core.Services.AdsPower;

public interface IAdsPowerApiClient
{
    Task<IReadOnlyList<AdsPowerProfileSummary>> ListProfilesAsync(
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

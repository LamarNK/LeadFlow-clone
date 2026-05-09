namespace LeadFlow.Services.AdsPower;

public interface IAdsPowerApiClient
{
    Task<IReadOnlyList<AdsPowerProfileSummary>> ListProfilesAsync(
        AdsPowerConnectionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Запускает браузер профиля AdsPower (GET /api/v1/browser/start).
    /// </summary>
    Task StartBrowserAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        string? openUrl,
        CancellationToken cancellationToken = default);
}

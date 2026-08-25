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

    Task<IReadOnlyList<MultiloginProfileSummary>> SearchProfilesAsync(
        MultiloginConnectionOptions options,
        CancellationToken cancellationToken = default);
}

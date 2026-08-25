namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Клиент launcher Multilogin X: только подтверждённые start/stop.
/// Список профилей не входит в контракт.
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
}

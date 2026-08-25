namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Клиент Multilogin X. На этом этапе контракт зафиксирован без HTTP-вызовов:
/// список профилей не включён, пока в репозитории нет подтверждённого JSON-fixture.
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

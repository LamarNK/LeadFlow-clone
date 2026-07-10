namespace Orbita.Contracts;

public static class BrowserMonitorCatalogBuilder
{
    public sealed record ActiveBrowserSeed(
        Guid AccountId,
        string AccountName,
        string AdsPowerProfileId);

    public static IReadOnlyList<BrowserMonitorBrowserDto> BuildActiveCatalog(
        IReadOnlyList<ActiveBrowserSeed> browsers,
        IReadOnlyDictionary<Guid, long> lastFrameAtByAccount,
        IReadOnlyDictionary<Guid, (string? SubProfileId, string? SubProfileName, string? PageUrl)> runtimeByAccount)
    {
        var index = 0;
        return browsers
            .Select(browser =>
            {
                index++;
                runtimeByAccount.TryGetValue(browser.AccountId, out var runtime);
                lastFrameAtByAccount.TryGetValue(browser.AccountId, out var lastFrameAtMs);

                return new BrowserMonitorBrowserDto(
                    browser.AccountId,
                    browser.AccountName,
                    browser.AdsPowerProfileId,
                    index,
                    BrowserMonitorStatuses.Running,
                    runtime.PageUrl,
                    StatusMessage: null,
                    runtime.SubProfileId,
                    runtime.SubProfileName,
                    lastFrameAtMs > 0 ? lastFrameAtMs : null);
            })
            .ToList();
    }
}
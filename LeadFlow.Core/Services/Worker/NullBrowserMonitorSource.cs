namespace LeadFlow.Core.Services.Worker;

public sealed class NullBrowserMonitorSource : IBrowserMonitorSource
{
    public static readonly NullBrowserMonitorSource Instance = new();

    public bool IsActive => false;

    public void Register(
        Guid accountId,
        string accountName,
        string adsPowerProfileId,
        Func<CancellationToken, Task<BrowserMonitorCapture?>> captureAsync)
    {
    }

    public void Unregister(Guid accountId)
    {
    }
}
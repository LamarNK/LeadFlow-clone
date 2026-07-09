namespace LeadFlow.Core.Services.Worker;

public sealed record BrowserMonitorCapture(
    byte[]? JpegBytes,
    string? PageUrl,
    string? SubProfileId = null,
    string? SubProfileName = null);

public interface IBrowserMonitorSource
{
    void Register(
        Guid accountId,
        string accountName,
        string adsPowerProfileId,
        Func<CancellationToken, Task<BrowserMonitorCapture?>> captureAsync);

    void Unregister(Guid accountId);

    bool IsActive { get; }
}
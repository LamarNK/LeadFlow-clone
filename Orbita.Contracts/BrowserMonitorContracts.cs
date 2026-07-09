namespace Orbita.Contracts;

public static class BrowserMonitorStatuses
{
    public const string Stopped = "stopped";
    public const string Running = "running";
    public const string Error = "error";
}

public sealed record BrowserMonitorBrowserDto(
    Guid AccountId,
    string AccountName,
    string AdsPowerProfileId,
    int Index,
    string Status,
    string? PageUrl = null,
    string? StatusMessage = null,
    string? SubProfileId = null,
    string? SubProfileName = null,
    long? LastFrameAtMs = null);

public sealed record BrowserMonitorSessionDto(
    Guid Id,
    Guid WorkerId,
    string WorkerName,
    string OperatorUserId,
    string OperatorDisplayName,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    IReadOnlyList<BrowserMonitorBrowserDto> Browsers);

public sealed record WorkerPendingBrowserMonitorSessionDto(
    Guid SessionId,
    Guid WorkerId,
    IReadOnlyList<BrowserMonitorBrowserDto> Browsers);

public sealed record BrowserMonitorCatalogMessage(
    Guid SessionId,
    IReadOnlyList<BrowserMonitorBrowserDto> Browsers,
    long TimestampMs);

public sealed record BrowserMonitorFrameMessage(
    Guid SessionId,
    Guid AccountId,
    string ImageBase64,
    int ViewportWidth,
    int ViewportHeight,
    long TimestampMs,
    string? PageUrl = null,
    string? SubProfileId = null,
    string? SubProfileName = null,
    string ContentType = "image/jpeg");
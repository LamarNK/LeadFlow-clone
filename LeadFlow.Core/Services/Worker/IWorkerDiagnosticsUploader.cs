namespace LeadFlow.Core.Services.Worker;

public interface IWorkerDiagnosticsUploader
{
    Task<Guid?> UploadScreenshotAsync(
        Guid accountId,
        byte[] png,
        string kind,
        string? pageUrl,
        CancellationToken cancellationToken = default);
}
namespace LeadFlow.Core.Services.Worker;

public sealed class NullWorkerDiagnosticsUploader : IWorkerDiagnosticsUploader
{
    public Task<Guid?> UploadScreenshotAsync(
        Guid accountId,
        byte[] png,
        string kind,
        string? pageUrl,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(null);
}
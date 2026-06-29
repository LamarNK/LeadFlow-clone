using System.Net.Http.Headers;
using LeadFlow.Core.Services.Worker;
using Orbita.Worker;

namespace Orbita.Worker.Services;

public sealed class DiagnosticsUploadService(
    OrbitaApiClient apiClient,
    WorkerCredentials credentials) : IWorkerDiagnosticsUploader
{
    public async Task<Guid?> UploadScreenshotAsync(
        Guid accountId,
        byte[] png,
        string kind,
        string? pageUrl,
        CancellationToken cancellationToken = default)
    {
        if (credentials.WorkerId is null || png.Length == 0)
        {
            return null;
        }

        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(accountId.ToString()), "accountId");
            content.Add(new StringContent(kind), "kind");
            if (!string.IsNullOrWhiteSpace(pageUrl))
            {
                content.Add(new StringContent(pageUrl), "pageUrl");
            }

            var fileContent = new ByteArrayContent(png);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(fileContent, "file", "screenshot.png");

            var payload = await apiClient.UploadDiagnosticAsync(content, cancellationToken).ConfigureAwait(false);
            return payload?.AttachmentId;
        }
        catch
        {
            return null;
        }
    }
}
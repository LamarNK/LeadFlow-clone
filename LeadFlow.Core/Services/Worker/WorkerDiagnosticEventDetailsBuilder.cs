using System.Text.Json;
using LeadFlow.Core.Services.Avito;

namespace LeadFlow.Core.Services.Worker;

public static class WorkerDiagnosticEventDetailsBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<WorkerDiagnosticEventDetails> BuildAsync(
        IWorkerDiagnosticsUploader uploader,
        Guid accountId,
        string kind,
        string text,
        string? pageUrl,
        byte[]? screenshotPng,
        string? subProfileId = null,
        string? subProfileName = null,
        CancellationToken cancellationToken = default,
        AvitoPageState? pageState = null,
        string? expectedStep = null,
        IReadOnlyList<string>? signals = null,
        string? pageTitle = null)
    {
        Guid? attachmentId = null;
        if (screenshotPng is { Length: > 0 })
        {
            attachmentId = await uploader
                .UploadScreenshotAsync(accountId, screenshotPng, kind, pageUrl, cancellationToken)
                .ConfigureAwait(false);
        }

        if (attachmentId is null
            && string.IsNullOrWhiteSpace(pageUrl)
            && string.IsNullOrWhiteSpace(subProfileId)
            && string.IsNullOrWhiteSpace(subProfileName)
            && string.IsNullOrWhiteSpace(pageTitle)
            && (signals is null || signals.Count == 0))
        {
            return new WorkerDiagnosticEventDetails(text, null);
        }

        var details = JsonSerializer.Serialize(new
        {
            attachmentId,
            kind,
            url = pageUrl,
            title = pageTitle,
            text,
            subProfileId,
            subProfileName,
            expectedStep,
            actualStep = pageState?.DescribeKindRu(),
            pageStateSummary = pageState?.DescribeForDiagnostics(),
            signals
        }, JsonOptions);
        return new WorkerDiagnosticEventDetails(details, attachmentId);
    }
}
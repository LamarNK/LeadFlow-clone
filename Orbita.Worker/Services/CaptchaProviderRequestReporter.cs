using System.Net.Http.Json;
using LeadFlow.Core.Services.Captcha;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

/// <summary>Worker-side bridge for the provider-request audit API.</summary>
public sealed class CaptchaProviderRequestReporter(
    OrbitaApiClient api,
    WorkerCredentials credentials,
    IWorkerDiagnosticsUploader diagnostics) : ICaptchaProviderRequestReporter
{
    public async Task<Guid?> CreateAsync(CaptchaProviderRequestSubmission request, byte[]? screenshotPng, CancellationToken cancellationToken = default)
    {
        if (credentials.WorkerId is not Guid workerId || request.AccountId == Guid.Empty) return null;
        Guid? attachmentId = null;
        if (screenshotPng is { Length: > 0 })
            attachmentId = await diagnostics.UploadScreenshotAsync(request.AccountId, screenshotPng, "captcha-provider-request", request.PageUrl, cancellationToken).ConfigureAwait(false);
        try
        {
            return await api.CreateCaptchaProviderRequestAsync(new CaptchaProviderRequestCreateDto(
                Guid.NewGuid(), request.AccountId, request.CycleRunId, request.SubProfileRunId, request.SubProfileId,
                request.SubProfileName, request.Provider, request.CaptchaType, request.Stage, request.Reason,
                request.Attempt, request.MaxAttempts, request.PageUrl, request.SubmittedAtUtc, attachmentId), cancellationToken).ConfigureAwait(false);
        }
        catch { return null; }
    }

    public Task MarkProviderAcceptedAsync(Guid requestId, string providerTaskId, CancellationToken cancellationToken = default) =>
        api.MarkCaptchaProviderResultAsync(new CaptchaProviderRequestProviderResultDto(requestId, "accepted", providerTaskId, null, DateTime.UtcNow), cancellationToken);

    public Task MarkProviderFailedAsync(Guid requestId, string providerStatus, string? errorCode, CancellationToken cancellationToken = default) =>
        api.MarkCaptchaProviderResultAsync(new CaptchaProviderRequestProviderResultDto(requestId, providerStatus, null, errorCode, DateTime.UtcNow), cancellationToken);

    public Task MarkTargetOutcomeAsync(Guid requestId, string targetStatus, CancellationToken cancellationToken = default) =>
        api.MarkCaptchaTargetResultAsync(new CaptchaProviderRequestTargetResultDto(requestId, targetStatus, DateTime.UtcNow), cancellationToken);
}

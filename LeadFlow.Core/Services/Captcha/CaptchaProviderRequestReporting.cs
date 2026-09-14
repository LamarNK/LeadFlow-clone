namespace LeadFlow.Core.Services.Captcha;

/// <summary>Этап браузерного сценария, на котором понадобилась задача провайдера капчи.</summary>
public static class CaptchaProviderRequestStages
{
    public const string Authentication = "authentication";
    public const string AuthorizationRecovery = "authorization_recovery";
    public const string SubProfileSwitch = "subprofile_switch";
    public const string ResponsesPage = "responses_page";
    public const string Other = "other";
}

/// <summary>Причина, по которой задача была отправлена провайдеру.</summary>
public static class CaptchaProviderRequestReasons
{
    public const string FirewallDetected = "firewall_detected";
    public const string LoginRequired = "login_required";
    public const string AfterSubProfileSwitch = "after_subprofile_switch";
    public const string OnResponsesPage = "on_responses_page";
    public const string RetryAfterProviderFailure = "retry_after_provider_failure";
    public const string RetryAfterTargetRejection = "retry_after_target_rejection";
}

public static class CaptchaProviderRequestProviderStatuses
{
    public const string Submitted = "submitted";
    public const string Accepted = "accepted";
    public const string NoSlot = "no_slot";
    public const string Error = "error";
}

public static class CaptchaProviderRequestTargetStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

public sealed record CaptchaProviderRequestContextValue(
    Guid WorkerId,
    Guid AccountId,
    Guid? CycleRunId,
    Guid? SubProfileRunId,
    string? SubProfileId,
    string? SubProfileName,
    string Stage,
    string Reason);

/// <summary>
/// Async-local связь запроса к провайдеру с текущим проходом мониторинга.
/// Нужна, чтобы автоматизация не передавала токены или HTML в статистику.
/// </summary>
public static class CaptchaProviderRequestContext
{
    private static readonly AsyncLocal<CaptchaProviderRequestContextValue?> CurrentValue = new();

    public static CaptchaProviderRequestContextValue? Current => CurrentValue.Value;

    public static IDisposable Use(CaptchaProviderRequestContextValue value)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = value;
        return new Scope(() => CurrentValue.Value = previous);
    }

    public static void SetCycle(Guid cycleRunId) =>
        CurrentValue.Value = CurrentValue.Value is { } value ? value with { CycleRunId = cycleRunId } : null;

    public static void SetSubProfile(Guid subProfileRunId, string? subProfileId, string? subProfileName) =>
        CurrentValue.Value = CurrentValue.Value is { } value
            ? value with { SubProfileRunId = subProfileRunId, SubProfileId = subProfileId, SubProfileName = subProfileName }
            : null;

    public static void SetStage(string stage, string reason) =>
        CurrentValue.Value = CurrentValue.Value is { } value ? value with { Stage = stage, Reason = reason } : null;

    private sealed class Scope(Action restore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                restore();
            }
        }
    }
}

public sealed record CaptchaProviderRequestSubmission(
    Guid AccountId,
    Guid? CycleRunId,
    Guid? SubProfileRunId,
    string? SubProfileId,
    string? SubProfileName,
    string Provider,
    string CaptchaType,
    string Stage,
    string Reason,
    int Attempt,
    int MaxAttempts,
    string? PageUrl,
    DateTime SubmittedAtUtc);

public interface ICaptchaProviderRequestReporter
{
    Task<Guid?> CreateAsync(
        CaptchaProviderRequestSubmission request,
        byte[]? screenshotPng,
        CancellationToken cancellationToken = default);

    Task MarkProviderAcceptedAsync(
        Guid requestId,
        string providerTaskId,
        CancellationToken cancellationToken = default);

    Task MarkProviderFailedAsync(
        Guid requestId,
        string providerStatus,
        string? errorCode,
        CancellationToken cancellationToken = default);

    Task MarkTargetOutcomeAsync(
        Guid requestId,
        string targetStatus,
        CancellationToken cancellationToken = default);
}

public sealed class NullCaptchaProviderRequestReporter : ICaptchaProviderRequestReporter
{
    public Task<Guid?> CreateAsync(CaptchaProviderRequestSubmission request, byte[]? screenshotPng, CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(null);

    public Task MarkProviderAcceptedAsync(Guid requestId, string providerTaskId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task MarkProviderFailedAsync(Guid requestId, string providerStatus, string? errorCode, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task MarkTargetOutcomeAsync(Guid requestId, string targetStatus, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

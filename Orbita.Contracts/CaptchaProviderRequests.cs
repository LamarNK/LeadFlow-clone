namespace Orbita.Contracts;

public sealed record CaptchaProviderRequestCreateDto(
    Guid Id,
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
    DateTime SubmittedAtUtc,
    Guid? DiagnosticAttachmentId = null);

public sealed record CaptchaProviderRequestProviderResultDto(
    Guid Id,
    string ProviderStatus,
    string? ProviderTaskId,
    string? ErrorCode,
    DateTime UpdatedAtUtc);

public sealed record CaptchaProviderRequestTargetResultDto(
    Guid Id,
    string TargetStatus,
    DateTime UpdatedAtUtc);

public sealed record CaptchaProviderRequestDetailsDto(
    Guid Id,
    Guid WorkerId,
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
    string ProviderStatus,
    string TargetStatus,
    string? ProviderTaskId,
    string? ErrorCode,
    string? PageUrl,
    Guid? DiagnosticAttachmentId,
    DateTime SubmittedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record CaptchaProviderWorkerStatisticsDto(
    Guid WorkerId,
    string WorkerName,
    Guid AccountId,
    string AccountName,
    int SubmittedCount,
    int ProviderAcceptedCount,
    int NoSlotCount,
    int ProviderErrorCount,
    int TargetAcceptedCount,
    int TargetRejectedCount,
    IReadOnlyList<CaptchaProviderRequestDetailsDto> Requests);

public sealed record CaptchaProviderStatisticsDto(
    int SubmittedCount,
    int ProviderAcceptedCount,
    int NoSlotCount,
    int ProviderErrorCount,
    int TargetAcceptedCount,
    int TargetRejectedCount,
    IReadOnlyList<CaptchaProviderWorkerStatisticsDto> Workers);

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
    Guid? DiagnosticAttachmentId = null,
    CaptchaContextDiagnosticsDto? Context = null);

public sealed record CaptchaContextDiagnosticsDto(
    string Source,
    string Fingerprint,
    bool ChallengePresent,
    bool RiskTypePresent,
    int ContextAgeMs);

public sealed record CaptchaProviderRequestProviderResultDto(
    Guid Id,
    string ProviderStatus,
    string? ProviderTaskId,
    string? ErrorCode,
    DateTime UpdatedAtUtc,
    int? SolveDurationMs = null);

public sealed record CaptchaProviderRequestTargetResultDto(
    Guid Id,
    string TargetStatus,
    DateTime UpdatedAtUtc,
    string? TargetReason = null,
    int? HttpStatus = null,
    int? ContextAgeMs = null);

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
    DateTime UpdatedAtUtc,
    string? ContextSource = null,
    string? ContextFingerprint = null,
    bool? ChallengePresent = null,
    bool? RiskTypePresent = null,
    int? ContextAgeAtSubmitMs = null,
    int? ContextAgeAtVerifyMs = null,
    int? SolveDurationMs = null,
    string? TargetReason = null,
    int? TargetHttpStatus = null);

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
    IReadOnlyList<CaptchaProviderWorkerStatisticsDto> Workers,
    int ContextDetectedCount = 0,
    int ChallengePresentCount = 0,
    int RiskTypePresentCount = 0);

namespace Orbita.Contracts;

public sealed record WorkerRegisterRequest(
    string RegistrationSecret,
    string DisplayName,
    string MachineName,
    string AppVersion);

public sealed record WorkerRegisterResponse(
    Guid WorkerId,
    string ApiKey);

public sealed record WorkerHeartbeatRequest(
    Guid WorkerId,
    string DisplayName,
    string AppVersion,
    string MachineName,
    string MonitoringStatus,
    string? MonitoringStatusMessage,
    bool IsMonitoringActive,
    DateTime? NextCycleCheckAtUtc,
    WorkerSystemMetricsDto? SystemMetrics = null,
    WorkerUpdateResultDto? LastUpdateResult = null,
    string? OperatingSystem = null,
    DateTime? StartedAtUtc = null,
    string? PublicIpAddress = null,
    string? AgentVersion = null);

public sealed record WorkerSnapshotRequest(
    Guid WorkerId,
    DateTime CapturedAtUtc,
    DashboardStatsDto Stats,
    IReadOnlyList<WorkerAccountDto> Accounts,
    IReadOnlyList<WorkerBalanceDto> Balances);

public sealed record DashboardStatsDto(
    int NewResponses,
    int TotalToday,
    int SentToCrm,
    int InProgress,
    int Duplicates,
    int Errors,
    int ActionRequired,
    int ConnectedAccounts,
    int RequiresAuthorization,
    int AccountsNeedAttentionCount,
    int ActiveAdsCount,
    int BlockedAdsCount,
    int DraftsCount,
    IReadOnlyList<ActivityPointDto> HourlyActivity,
    IReadOnlyList<ActivityPointDto> WeeklyByDayActivity);

public sealed record ActivityPointDto(
    string Label,
    int NewCount,
    int SentCount,
    int DuplicateCount,
    int ErrorCount,
    int SlotStartHour,
    int SlotSpanHours,
    DateTime? LocalDate);

public sealed record WorkerSubProfileDto(
    string Id,
    string Name,
    string Category,
    bool IsCurrent,
    decimal? Balance,
    string? LastIssueKind,
    string? LastIssueMessage,
    DateTime? LastIssueAt,
    bool IsEnabledInPanel = true,
    Guid? DiagnosticAttachmentId = null,
    decimal? WalletBalance = null,
    string? AdvanceDurationText = null,
    decimal? Rating = null,
    int? ReviewsCount = null,
    string? ReviewsText = null,
    int TodayResponses = 0,
    int TodayDuplicates = 0,
    int TodayEventErrors = 0,
    DateTime? LastActivityUtc = null);

public sealed record WorkerAccountDto(
    Guid AccountId,
    string DisplayName,
    string Status,
    bool IsEnabled,
    int ActiveAdsCount,
    int BlockedCount,
    int DraftsCount,
    string? LastErrorMessage,
    DateTime? LastMonitoringAt,
    bool IsEnabledInPanel = false,
    string AdsPowerProfileId = "",
    IReadOnlyList<WorkerSubProfileDto>? SubProfiles = null,
    DateTime? SubProfilesRefreshedAtUtc = null,
    DateTime? SubProfilesRefreshRequestedAtUtc = null,
    int TodayResponses = 0,
    int TodayDuplicates = 0,
    int TodayEventErrors = 0);

public sealed record WorkerBalanceDto(
    Guid AccountId,
    string AccountName,
    decimal TotalBalance,
    IReadOnlyList<SubProfileBalanceDto> SubProfiles,
    decimal TotalWalletBalance = 0);

public sealed record SubProfileBalanceDto(
    string SubProfileName,
    decimal? Balance,
    decimal? WalletBalance = null,
    string? AdvanceDurationText = null);

public sealed record WorkerEventBatchRequest(
    Guid WorkerId,
    IReadOnlyList<WorkerEventDto> Events);

public sealed record WorkerEventDto(
    Guid? AccountId,
    string Level,
    string Message,
    string? Details,
    DateTime CreatedAtUtc);

public static class WorkerActivityPhases
{
    public const string Idle = "idle";
    public const string Waiting = "waiting";
    public const string Cycle = "cycle";
    public const string Account = "account";
    public const string SubProfile = "subprofile";
    public const string Skipped = "skipped";
    public const string Error = "error";
    public const string Stopped = "stopped";
    public const string Parallel = "parallel";
}

public sealed record WorkerActiveAccountDto(
    Guid AccountId,
    string AccountName,
    string Phase,
    string Message,
    string? SubProfileId = null,
    string? SubProfileName = null,
    DateTime UpdatedAtUtc = default);

public sealed record WorkerActivityRequest(
    Guid WorkerId,
    string Phase,
    string Message,
    Guid? AccountId = null,
    string? AccountName = null,
    string? SubProfileId = null,
    string? SubProfileName = null,
    DateTime? NextCycleAtUtc = null,
    DateTime UpdatedAtUtc = default,
    IReadOnlyList<WorkerActiveAccountDto>? ActiveAccounts = null);

public sealed record WorkerActivityDto(
    string Phase,
    string Message,
    Guid? AccountId,
    string? AccountName,
    string? SubProfileId,
    string? SubProfileName,
    DateTime? NextCycleAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<WorkerActiveAccountDto>? ActiveAccounts = null);
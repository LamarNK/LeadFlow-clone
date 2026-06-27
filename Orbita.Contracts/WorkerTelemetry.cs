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
    string AdsPowerProfileId = "");

public sealed record WorkerBalanceDto(
    Guid AccountId,
    string AccountName,
    decimal TotalBalance,
    IReadOnlyList<SubProfileBalanceDto> SubProfiles);

public sealed record SubProfileBalanceDto(
    string SubProfileName,
    decimal? Balance);

public sealed record WorkerEventBatchRequest(
    Guid WorkerId,
    IReadOnlyList<WorkerEventDto> Events);

public sealed record WorkerEventDto(
    Guid? AccountId,
    string Level,
    string Message,
    string? Details,
    DateTime CreatedAtUtc);
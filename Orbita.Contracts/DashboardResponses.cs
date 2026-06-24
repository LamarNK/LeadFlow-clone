namespace Orbita.Contracts;

public sealed record GlobalDashboardSummary(
    int TotalWorkers,
    int OnlineWorkers,
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
    decimal TotalBalance,
    IReadOnlyList<ActivityPointDto> HourlyActivity,
    IReadOnlyList<ActivityPointDto> WeeklyByDayActivity,
    DateTime AggregatedAtUtc);

public sealed record WorkerListItem(
    Guid Id,
    string DisplayName,
    string MachineName,
    string AppVersion,
    string MonitoringStatus,
    string? MonitoringStatusMessage,
    bool IsMonitoringActive,
    bool IsOnline,
    DateTime? LastSeenAtUtc,
    int AccountCount,
    int TotalToday,
    int Errors);

public sealed record WorkerDetail(
    Guid Id,
    string DisplayName,
    string MachineName,
    string AppVersion,
    string MonitoringStatus,
    string? MonitoringStatusMessage,
    bool IsMonitoringActive,
    bool IsOnline,
    DateTime? LastSeenAtUtc,
    DateTime? NextCycleCheckAtUtc,
    DashboardStatsDto? LatestStats,
    IReadOnlyList<WorkerBalanceDto> Balances);

public sealed record WorkerEventListItem(
    Guid Id,
    Guid WorkerId,
    string WorkerDisplayName,
    Guid? AccountId,
    string Level,
    string Message,
    string? Details,
    DateTime CreatedAtUtc);
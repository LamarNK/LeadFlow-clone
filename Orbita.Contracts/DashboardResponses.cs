namespace Orbita.Contracts;

public sealed record DashboardAccountStatusCounts(
    int Active,
    int Inactive,
    int Blocked,
    int Errors);

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
    DashboardAccountStatusCounts AccountStatusCounts,
    int ActiveAdsCount,
    int BlockedAdsCount,
    decimal TotalBalance,
    IReadOnlyList<ActivityPointDto> HourlyActivity,
    IReadOnlyList<ActivityPointDto> WeeklyByDayActivity,
    DateTime AggregatedAtUtc)
{
    public int UniqueResponsesToday => Math.Max(0, TotalToday - Duplicates);
}

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
    int DuplicatesToday,
    int Errors,
    bool UpdateAvailable = false,
    string? LatestReleaseVersion = null,
    Guid OfficeId = default,
    string OfficeName = "",
    bool IsEnabled = true,
    int ActiveAccountCount = 0,
    int LowBalanceAccountCount = 0,
    WorkerActivityDto? CurrentActivity = null,
    IReadOnlyList<WorkerActiveAccountDto>? ActiveAccounts = null,
    bool IsMonitoringPaused = false,
    string IpAddress = "");

public sealed record WorkersPageDto(
    IReadOnlyList<WorkerListItem> Items,
    int TotalCount,
    int Page,
    int PageSize,
    string Sort,
    string Dir,
    int EnabledCount,
    int PausedCount)
{
    public DashboardWorkerTabCounts TabCounts { get; init; } = DashboardWorkerTabCounts.None;
}

public sealed record DashboardWorkerTabCounts(
    int All,
    int Online,
    int Offline,
    int Empty,
    int Paused)
{
    public static DashboardWorkerTabCounts None { get; } = new(0, 0, 0, 0, 0);
}

public static class DashboardWorkerFilter
{
    public const string All = "all";
    public const string Online = "online";
    public const string Offline = "offline";
    public const string Empty = "empty";
    public const string Paused = "paused";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Online => Online,
        Offline => Offline,
        Empty => Empty,
        Paused => Paused,
        _ => All
    };

    public static bool Matches(WorkerListItem worker, string filter) => filter switch
    {
        Online => worker.IsEnabled && worker.IsOnline,
        Offline => !worker.IsEnabled || !worker.IsOnline,
        // The dashboard's "Нет аккаунтов" tab also surfaces workers whose
        // configured accounts are all inactive. Both states require attention.
        Empty => worker.AccountCount == 0 || worker.ActiveAccountCount == 0,
        Paused => worker.IsMonitoringPaused,
        _ => true
    };

    public static DashboardWorkerTabCounts Count(IReadOnlyList<WorkerListItem> workers) => new(
        workers.Count,
        workers.Count(worker => Matches(worker, Online)),
        workers.Count(worker => Matches(worker, Offline)),
        workers.Count(worker => Matches(worker, Empty)),
        workers.Count(worker => Matches(worker, Paused)));
}

public static class WorkerListPaging
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const string DefaultSort = "activity";

    public static readonly HashSet<string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "activity", "responses", "errors"
    };

    public static int NormalizePageSize(int? pageSize)
    {
        if (pageSize is null or <= 0)
        {
            return DefaultPageSize;
        }

        return Math.Clamp(pageSize.Value, 1, MaxPageSize);
    }

    public static int NormalizePage(int page, int pageSize, int totalCount)
    {
        if (page < 1)
        {
            page = 1;
        }

        if (totalCount <= 0 || pageSize <= 0)
        {
            return 1;
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        return Math.Min(page, totalPages);
    }

    public static (string Column, bool Descending) NormalizeSort(string? sort, string? dir)
    {
        var column = string.IsNullOrWhiteSpace(sort) || !SortColumns.Contains(sort)
            ? DefaultSort
            : sort;
        var descending = dir?.Trim().ToLowerInvariant() switch
        {
            "asc" => false,
            "desc" => true,
            _ => !string.Equals(column, "name", StringComparison.OrdinalIgnoreCase)
        };
        return (column, descending);
    }
}

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
    IReadOnlyList<WorkerBalanceDto> Balances,
    int MaxConcurrentAccounts = 1,
    double? LastCpuPercent = null,
    double? LastRamPercent = null,
    long? LastRamUsedMb = null,
    long? LastRamTotalMb = null,
    string? IpAddress = null,
    string? OperatingSystem = null,
    DateTime? StartedAtUtc = null,
    string? AgentVersion = null,
    string? AdsPowerApiBaseUrl = null,
    string? AdsPowerApiKey = null,
    bool IsEnabled = true,
    int TodayResponses = 0,
    int TodayDuplicates = 0,
    int TodayErrors = 0,
    int ActiveAccountCount = 0,
    int TotalAccountCount = 0,
    WorkerActivityDto? CurrentActivity = null,
    IReadOnlyList<WorkerActiveAccountDto>? ActiveAccounts = null,
    bool ResponseFilterEnabled = false,
    bool ResponseFilterExcludeFemale = false,
    int? ResponseFilterMaxAge = null,
    bool ResponseFilterExcludeMale = false,
    int? ResponseFilterMaxAgeMale = null,
    int? ResponseFilterMaxAgeFemale = null,
    int? ResponseFilterMaxResponseAgeDays = null,
    bool ResponseHighlightEnabled = false,
    string? ResponseHighlightAgeBuckets = null,
    bool AutoScheduleEnabled = false,
    string? AutoScheduleDays = null,
    string? AutoScheduleFromLocalTime = null,
    string? AutoScheduleToLocalTime = null,
    bool MessengerAutoReplyEnabled = false,
    string? MessengerAutoReplyMessage = null,
    int? PhoneUnchangedHours = null,
    bool AutoDeliverToCrm = false,
    bool AutoDeliverToBitrix = true,
    Guid OfficeId = default,
    string OfficeName = "",
    string? ResponseHighlightTargetsJson = null,
    string? AdsPowerGroupId = null,
    string? AdsPowerGroupName = null,
    IReadOnlyList<AdsPowerGroupDto>? AdsPowerGroups = null,
    string? RuCaptchaApiKey = null,
    string? MultiloginLauncherUrl = null,
    string? MultiloginCloudApiUrl = null,
    bool HasMultiloginAutomationToken = false,
    string? LocalChromeExecutablePath = null,
    bool AdsPowerEnabled = true,
    bool MultiloginEnabled = true,
    bool LocalChromeEnabled = true,
    WorkerBrowserProviderCheckDto? AdsPowerCheck = null,
    WorkerBrowserProviderCheckDto? MultiloginCheck = null,
    WorkerBrowserProviderCheckDto? LocalChromeCheck = null,
    Guid? PendingLocalChromeLoginAccountId = null,
    bool IsMonitoringPaused = false);

/// <summary>
/// One row of the office-wide accounts page: account payload plus the worker
/// context and latest balance so the panel does not N+1 worker detail calls.
/// </summary>
public sealed record OfficeAccountListItem(
    Guid WorkerId,
    string WorkerDisplayName,
    string OfficeName,
    bool WorkerIsOnline,
    WorkerActivityDto? CurrentActivity,
    IReadOnlyList<WorkerActiveAccountDto>? ActiveAccounts,
    WorkerAccountDto Account,
    WorkerBalanceDto? Balance);

public sealed record WorkerEventListItem(
    Guid Id,
    Guid WorkerId,
    string WorkerDisplayName,
    Guid? AccountId,
    string? AccountDisplayName,
    string Level,
    string Message,
    string? Details,
    DateTime CreatedAtUtc);

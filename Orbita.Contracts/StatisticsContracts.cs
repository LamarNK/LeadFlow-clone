namespace Orbita.Contracts;

public sealed record OfficeStatisticsDto(
    BalanceStatisticsSection Balances,
    AccountInfrastructureSection Accounts,
    WorkerInfrastructureSection Workers,
    ResponsesPeriodSection Responses,
    IReadOnlyList<DailyResponseBucketDto> DailyTrend,
    IReadOnlyList<BitrixDeliveryStatDto> BitrixDeliveries,
    IReadOnlyList<CrmDeliveryStatDto> CrmDeliveries,
    HrInsightsDto HrInsights,
    MonitoringCycleReportDto MonitoringCycles,
    DateTime AggregatedAtUtc);

public sealed record BitrixDeliveryStatDto(
    Guid BitrixInstanceId,
    string Label,
    int SentCount);

public sealed record CrmDeliveryStatDto(
    Guid OfficeId,
    string Label,
    int SentCount);

public sealed record BalanceStatisticsSection(
    decimal TotalAdvance,
    decimal TotalWallet,
    int LowBalanceAccountCount,
    IReadOnlyList<AccountBalanceStatDto> Accounts);

public sealed record AccountBalanceStatDto(
    Guid AccountId,
    string AccountName,
    Guid WorkerId,
    string WorkerName,
    string OfficeName,
    decimal Advance,
    decimal Wallet,
    IReadOnlyList<SubProfileBalanceDto> SubProfiles,
    bool IsLowBalance);

public sealed record AccountInfrastructureSection(
    int Total,
    DashboardAccountStatusCounts StatusCounts,
    int ActiveAdsCount,
    int BlockedAdsCount);

public sealed record WorkerInfrastructureSection(
    int Total,
    int Online,
    IReadOnlyList<WorkerStatisticsRowDto> Items);

public sealed record WorkerStatisticsRowDto(
    Guid Id,
    string DisplayName,
    string OfficeName,
    bool IsOnline,
    int PeriodResponses,
    int PeriodSent,
    int PeriodDuplicates,
    int PeriodErrors,
    int ActiveAccounts,
    int TotalAccounts);

public sealed record ResponsesPeriodSection(
    int Total,
    int Unique,
    int Duplicates,
    int Sent,
    int InProgress,
    int ActionRequired,
    int Errors,
    int UniqueAuthors,
    double? AvgResponseMinutes);

public sealed record DailyResponseBucketDto(
    DateTime DateLocal,
    int Total,
    int Sent,
    int InProgress,
    int ActionRequired,
    int Duplicates,
    int Errors);

public sealed record HrInsightsDto(
    IReadOnlyList<HrMetricDto> TopCities,
    IReadOnlyList<HrMetricDto> TopVacancies,
    IReadOnlyList<HrMetricDto> TopAccounts,
    IReadOnlyList<AgeBucketDto> AgeBuckets,
    string AverageAgeText,
    string MessengerCoverageText);

public sealed record HrMetricDto(
    string Name,
    int Total,
    int Sent,
    string ConversionText,
    string ShareText);

public sealed record AgeBucketDto(
    string Bucket,
    int Total,
    int Sent,
    string ConversionText);

public sealed record MonitoringCycleErrorDto(
    DateTime TimestampUtc,
    string Detail);

public sealed record MonitoringCycleCaptchaDto(
    DateTime TimestampUtc,
    string Status,
    bool Unsolved = false);

public sealed record MonitoringCyclePassDto(
    DateTime TimestampUtc,
    bool Completed,
    int CollectedCount,
    bool HasCollected,
    string? CaptchaStatus = null,
    bool CaptchaUnsolved = false,
    string? ErrorDetail = null,
    bool InProgress = false,
    bool Skipped = false,
    bool LoginRequired = false,
    bool LoginAttempted = false,
    bool LoginSucceeded = false,
    int WatchRefreshedCount = 0,
    int PhoneChangedCount = 0);

public sealed record MonitoringCycleSubProfileRowDto(
    int Position,
    int TotalPositions,
    string Name,
    IReadOnlyList<DateTime> CompletionTimesUtc,
    IReadOnlyList<string> LeadsPerCycle,
    IReadOnlyList<MonitoringCycleErrorDto> Errors,
    bool WasStarted = false,
    IReadOnlyList<MonitoringCycleCaptchaDto>? CaptchaPerCycle = null,
    IReadOnlyList<MonitoringCyclePassDto>? Passes = null,
    string? NotStartedReason = null,
    DateTime? NotStartedAtUtc = null);

public sealed record MonitoringCycleAccountReportDto(
    string AccountName,
    DateTime DateUtc,
    int SubProfileCount,
    int CycleCount,
    int TotalLeads,
    IReadOnlyList<MonitoringCycleSubProfileRowDto> Rows,
    IReadOnlyList<string> NotStartedPositions,
    int TotalCaptcha = 0,
    int TotalCaptchaSolved = 0);

public sealed record MonitoringCycleLeadSummaryDto(
    string AccountName,
    int TotalLeads,
    IReadOnlyList<string> Breakdown);

public sealed record MonitoringCycleReportDto(
    bool IsDetailed,
    int TotalLeads,
    int AccountsWithNotStarted,
    int NotStartedPositions,
    IReadOnlyList<string> NotStartedSummaries,
    IReadOnlyList<MonitoringCycleLeadSummaryDto> LeadSummaries,
    IReadOnlyList<MonitoringCycleAccountReportDto> AccountReports,
    int TotalCaptcha = 0,
    int TotalCaptchaSolved = 0);

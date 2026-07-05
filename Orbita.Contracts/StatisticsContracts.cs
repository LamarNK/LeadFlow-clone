namespace Orbita.Contracts;

public sealed record OfficeStatisticsDto(
    BalanceStatisticsSection Balances,
    AccountInfrastructureSection Accounts,
    WorkerInfrastructureSection Workers,
    ResponsesPeriodSection Responses,
    IReadOnlyList<DailyResponseBucketDto> DailyTrend,
    HrInsightsDto HrInsights,
    DateTime AggregatedAtUtc);

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
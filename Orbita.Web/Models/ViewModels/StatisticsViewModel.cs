namespace Orbita.Web.Models.ViewModels;

public sealed class StatisticsViewModel
{
    public PageHeaderViewModel Header { get; init; } = new() { Title = "Статистика" };
    public StatisticsFiltersViewModel Filters { get; init; } = new();
    public IReadOnlyList<EventFilterOptionViewModel> WorkerOptions { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> AccountOptions { get; init; } = [];
    public bool HasActiveFilters { get; init; }
    public IReadOnlyList<ActiveFilterChipViewModel> ActiveFilterChips { get; init; } = [];
    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<StatisticsBalanceRowViewModel> BalanceRows { get; init; } = [];
    public StatisticsChartsViewModel Charts { get; init; } = new();
    public AccountStatsViewModel AccountStats { get; init; } = AccountStatsViewModel.Empty;
    public IReadOnlyList<StatisticsWorkerRowViewModel> Workers { get; init; } = [];
    public HrInsightsViewModel HrInsights { get; init; } = HrInsightsViewModel.Empty;
    public StatisticsSummaryViewModel Summary { get; init; } = new();
    public string? ErrorMessage { get; init; }
    public bool ShowOfficeColumn { get; init; }
}

public sealed class StatisticsLiveSnapshotViewModel
{
    public DateTime UpdatedAtUtc { get; init; }
    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<StatisticsBalanceRowViewModel> BalanceRows { get; init; } = [];
    public StatisticsChartsViewModel Charts { get; init; } = new();
    public AccountStatsViewModel AccountStats { get; init; } = AccountStatsViewModel.Empty;
    public IReadOnlyList<StatisticsWorkerRowViewModel> Workers { get; init; } = [];
    public HrInsightsViewModel HrInsights { get; init; } = HrInsightsViewModel.Empty;
    public StatisticsSummaryViewModel Summary { get; init; } = new();
}

public sealed class StatisticsSummaryViewModel
{
    public int ActiveAdsCount { get; init; }
    public int BlockedAdsCount { get; init; }
    public int PeriodTotal { get; init; }
    public int PeriodSent { get; init; }
    public int PeriodDuplicates { get; init; }
    public int PeriodErrors { get; init; }
    public int PeriodUniqueAuthors { get; init; }
    public int WorkersOnline { get; init; }
    public int WorkersTotal { get; init; }
    public string TotalAdvanceText { get; init; } = "—";
    public string? AvgResponseMinutesText { get; init; }
}

public sealed class StatisticsBalanceRowViewModel
{
    public Guid AccountId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = string.Empty;
    public string OfficeName { get; init; } = string.Empty;
    public decimal Advance { get; init; }
    public decimal Wallet { get; init; }
    public string AdvanceText { get; init; } = "—";
    public string WalletText { get; init; } = "—";
    public string? BalanceBreakdown { get; init; }
    public string? BalanceSubtitle { get; init; }
    public IReadOnlyList<StatisticsSubProfileBalanceViewModel> SubProfiles { get; init; } = [];
    public bool IsLowBalance { get; init; }
    public double BarWidth { get; init; }
}

public sealed class StatisticsSubProfileBalanceViewModel
{
    public string Name { get; init; } = string.Empty;
    public string AdvanceText { get; init; } = "—";
    public string? WalletText { get; init; }
    public string? DurationText { get; init; }
    public bool IsLowBalance { get; init; }
    public double BarWidth { get; init; }
}

public sealed class StatisticsFiltersViewModel
{
    public IReadOnlyList<Guid> WorkerIds { get; init; } = [];
    public IReadOnlyList<Guid> AccountIds { get; init; } = [];
}

public sealed class StatisticsWorkerRowViewModel
{
    public Guid Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string OfficeName { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public int PeriodResponses { get; init; }
    public int PeriodSent { get; init; }
    public int PeriodDuplicates { get; init; }
    public int PeriodErrors { get; init; }
    public int ActiveAccounts { get; init; }
    public int TotalAccounts { get; init; }
}

public sealed class HrInsightsViewModel
{
    public static HrInsightsViewModel Empty { get; } = new();

    public IReadOnlyList<HrMetricRowViewModel> TopCities { get; init; } = [];
    public IReadOnlyList<HrMetricRowViewModel> TopVacancies { get; init; } = [];
    public IReadOnlyList<HrMetricRowViewModel> TopAccounts { get; init; } = [];
    public IReadOnlyList<AgeBucketRowViewModel> AgeBuckets { get; init; } = [];
    public string AverageAgeText { get; init; } = "н/д";
    public string MessengerCoverageText { get; init; } = "0%";
}

public sealed class HrMetricRowViewModel
{
    public string Name { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Sent { get; init; }
    public string ConversionText { get; init; } = "0%";
    public string ShareText { get; init; } = "0%";
}

public sealed class AgeBucketRowViewModel
{
    public string Bucket { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Sent { get; init; }
    public string ConversionText { get; init; } = "0%";
}

public sealed class StatisticsChartsViewModel
{
    public StackedDailyChartViewModel DailyTrend { get; init; } = new();
    public DonutChartViewModel AccountStatus { get; init; } = new();
}

public sealed class StackedDailyChartViewModel
{
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<int> Sent { get; init; } = [];
    public IReadOnlyList<int> InProgress { get; init; } = [];
    public IReadOnlyList<int> ActionRequired { get; init; } = [];
    public IReadOnlyList<int> Duplicates { get; init; } = [];
    public IReadOnlyList<int> Errors { get; init; } = [];
    public IReadOnlyList<int> Totals { get; init; } = [];
}
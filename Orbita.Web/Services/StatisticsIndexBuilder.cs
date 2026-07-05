using System.Globalization;
using Orbita.Contracts;
using Orbita.Web.Formatting;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class StatisticsIndexBuilder
{
    public static StatisticsViewModel Build(
        OfficeStatisticsDto data,
        DashboardPeriod period,
        IOfficeContext officeContext,
        StatisticsFiltersViewModel filters,
        IReadOnlyList<EventFilterOptionViewModel> workerOptions,
        IReadOnlyList<EventFilterOptionViewModel> accountOptions,
        IReadOnlyList<ActiveFilterChipViewModel> activeFilterChips)
    {
        var accountStats = new AccountStatsViewModel
        {
            Total = data.Accounts.Total,
            Active = data.Accounts.StatusCounts.Active,
            Inactive = data.Accounts.StatusCounts.Inactive,
            Blocked = data.Accounts.StatusCounts.Blocked,
            Errors = data.Accounts.StatusCounts.Errors
        };

        var maxBalance = data.Balances.Accounts.Count > 0
            ? Math.Max(1m, data.Balances.Accounts.Max(a => a.Advance))
            : 1m;

        var balanceRows = data.Balances.Accounts
            .Select(a =>
            {
                var subProfiles = MapSubProfileBalances(a.SubProfiles);
                var durationHint = BalanceDisplay.ResolveAdvanceDurationHint(
                    a.SubProfiles.Select(s => (s.Balance, s.AdvanceDurationText)).ToList());
                return new StatisticsBalanceRowViewModel
                {
                    AccountId = a.AccountId,
                    AccountName = a.AccountName,
                    WorkerId = a.WorkerId,
                    WorkerName = a.WorkerName,
                    OfficeName = a.OfficeName,
                    Advance = a.Advance,
                    Wallet = a.Wallet,
                    AdvanceText = BalanceDisplay.FormatAmount(a.Advance),
                    WalletText = a.Wallet > 0 ? BalanceDisplay.FormatAmount(a.Wallet) : "—",
                    BalanceBreakdown = SubProfileViewModelMapper.BuildBalanceBreakdown(
                        SubProfileViewModelMapper.MapFromBalances(a.SubProfiles)),
                    BalanceSubtitle = subProfiles.Count == 0
                        ? BalanceDisplay.FormatAccountBreakdown(
                            a.Wallet > 0 ? a.Wallet : null,
                            durationHint)
                        : null,
                    SubProfiles = subProfiles,
                    IsLowBalance = a.IsLowBalance,
                    BarWidth = (double)(a.Advance / maxBalance)
                };
            })
            .ToList();

        var periodErrors = data.Responses.Errors + data.Responses.ActionRequired;
        var summary = new StatisticsSummaryViewModel
        {
            ActiveAdsCount = data.Accounts.ActiveAdsCount,
            BlockedAdsCount = data.Accounts.BlockedAdsCount,
            PeriodTotal = data.Responses.Total,
            PeriodSent = data.Responses.Sent,
            PeriodDuplicates = data.Responses.Duplicates,
            PeriodErrors = periodErrors,
            PeriodUniqueAuthors = data.Responses.UniqueAuthors,
            WorkersOnline = data.Workers.Online,
            WorkersTotal = data.Workers.Total,
            TotalAdvanceText = BalanceDisplay.FormatAmount(data.Balances.TotalAdvance),
            AvgResponseMinutesText = data.Responses.AvgResponseMinutes is double minutes
                ? $"{Math.Round(minutes, 0):0} мин"
                : null
        };

        var header = PageHeaderBuilder.WithOfficeScope(
            PageHeaderBuilder.Statistics(period),
            officeContext);

        return new StatisticsViewModel
        {
            Header = header,
            Filters = filters,
            WorkerOptions = workerOptions,
            AccountOptions = accountOptions,
            HasActiveFilters = HasActiveFilters(filters),
            ActiveFilterChips = activeFilterChips,
            KpiCards = BuildKpiCards(data, period, filters),
            BalanceRows = balanceRows,
            Charts = BuildCharts(data, accountStats),
            AccountStats = accountStats,
            Workers = data.Workers.Items
                .Select(w => new StatisticsWorkerRowViewModel
                {
                    Id = w.Id,
                    DisplayName = w.DisplayName,
                    OfficeName = w.OfficeName,
                    IsOnline = w.IsOnline,
                    PeriodResponses = w.PeriodResponses,
                    PeriodSent = w.PeriodSent,
                    PeriodDuplicates = w.PeriodDuplicates,
                    PeriodErrors = w.PeriodErrors,
                    ActiveAccounts = w.ActiveAccounts,
                    TotalAccounts = w.TotalAccounts
                })
                .ToList(),
            HrInsights = MapHrInsights(data.HrInsights),
            Summary = summary,
            ShowOfficeColumn = officeContext.ShowOfficeColumn
        };
    }

    public static bool HasActiveFilters(StatisticsFiltersViewModel filters) =>
        filters.WorkerIds.Count > 0 || filters.AccountIds.Count > 0;

    private static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(
        OfficeStatisticsDto data,
        DashboardPeriod period,
        StatisticsFiltersViewModel filters)
    {
        var totalResponses = Math.Max(1, data.Responses.Total);
        var periodErrors = data.Responses.Errors + data.Responses.ActionRequired;
        string Pct(int value) => $"{value * 100.0 / totalResponses:0.#}%";
        var sentShare = data.Responses.Total == 0
            ? "0%"
            : Pct(data.Responses.Sent);

        return
        [
            new()
            {
                Key = "responses",
                Href = KpiCardLinks.StatisticsCard("responses", period.From, period.To, filters),
                Label = "Откликов",
                Value = data.Responses.Total.ToString(),
                CountValue = data.Responses.Total,
                Delta = period.Label,
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-comments",
                IconTone = "blue"
            },
            new()
            {
                Key = "sent",
                Href = KpiCardLinks.StatisticsCard("sent", period.From, period.To, filters),
                Label = "В Битрикс24",
                Value = data.Responses.Sent.ToString(),
                CountValue = data.Responses.Sent,
                Delta = sentShare,
                DeltaTone = data.Responses.Sent > 0 ? "good" : "neutral",
                IconClass = "fa-solid fa-paper-plane",
                IconTone = "green"
            },
            new()
            {
                Key = "duplicates",
                Href = KpiCardLinks.StatisticsCard("duplicates", period.From, period.To, filters),
                Label = "Дублей",
                Value = data.Responses.Duplicates.ToString(),
                CountValue = data.Responses.Duplicates,
                Delta = Pct(data.Responses.Duplicates),
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-clone",
                IconTone = "orange"
            },
            new()
            {
                Key = "errors",
                Href = KpiCardLinks.StatisticsCard("errors", period.From, period.To, filters),
                Label = "Ошибок",
                Value = periodErrors.ToString(),
                CountValue = periodErrors,
                Delta = Pct(periodErrors),
                DeltaTone = periodErrors > 0 ? "bad" : "good",
                IconClass = "fa-solid fa-triangle-exclamation",
                IconTone = "orange"
            },
            new()
            {
                Key = "unique_authors",
                Href = KpiCardLinks.StatisticsCard("unique_authors", period.From, period.To, filters),
                Label = "Уникальных авторов",
                Value = data.Responses.UniqueAuthors.ToString(),
                CountValue = data.Responses.UniqueAuthors,
                Delta = data.Responses.Total == 0
                    ? "0%"
                    : $"{data.Responses.UniqueAuthors * 100.0 / data.Responses.Total:0.#}% от откликов",
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-user-group",
                IconTone = "purple"
            }
        ];
    }

    private static StatisticsChartsViewModel BuildCharts(
        OfficeStatisticsDto data,
        AccountStatsViewModel accountStats)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        return new StatisticsChartsViewModel
        {
            DailyTrend = new StackedDailyChartViewModel
            {
                Labels = data.DailyTrend
                    .Select(d => d.DateLocal.ToString("dd.MM", culture))
                    .ToList(),
                Sent = data.DailyTrend.Select(d => d.Sent).ToList(),
                InProgress = data.DailyTrend.Select(d => d.InProgress).ToList(),
                ActionRequired = data.DailyTrend.Select(d => d.ActionRequired).ToList(),
                Duplicates = data.DailyTrend.Select(d => d.Duplicates).ToList(),
                Errors = data.DailyTrend.Select(d => d.Errors).ToList(),
                Totals = data.DailyTrend.Select(d => d.Total).ToList()
            },
            AccountStatus = new DonutChartViewModel
            {
                Total = accountStats.Total,
                Active = accountStats.Active,
                Inactive = accountStats.Inactive,
                Blocked = accountStats.Blocked,
                Errors = accountStats.Errors
            }
        };
    }

    private static HrInsightsViewModel MapHrInsights(HrInsightsDto insights) =>
        new()
        {
            TopCities = MapHrMetrics(insights.TopCities),
            TopVacancies = MapHrMetrics(insights.TopVacancies),
            TopAccounts = MapHrMetrics(insights.TopAccounts),
            AgeBuckets = insights.AgeBuckets
                .Select(b => new AgeBucketRowViewModel
                {
                    Bucket = b.Bucket,
                    Total = b.Total,
                    Sent = b.Sent,
                    ConversionText = b.ConversionText
                })
                .ToList(),
            AverageAgeText = insights.AverageAgeText,
            MessengerCoverageText = insights.MessengerCoverageText
        };

    private static IReadOnlyList<HrMetricRowViewModel> MapHrMetrics(IReadOnlyList<HrMetricDto> rows) =>
        rows.Select(r => new HrMetricRowViewModel
        {
            Name = r.Name,
            Total = r.Total,
            Sent = r.Sent,
            ConversionText = r.ConversionText,
            ShareText = r.ShareText
        }).ToList();

    private static IReadOnlyList<StatisticsSubProfileBalanceViewModel> MapSubProfileBalances(
        IReadOnlyList<SubProfileBalanceDto> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var maxAdvance = Math.Max(1m, items.Max(s => s.Balance ?? 0m));

        return items
            .Select((item, index) =>
            {
                var name = string.IsNullOrWhiteSpace(item.SubProfileName)
                    ? items.Count == 1 ? "Субпрофиль" : $"Субпрофиль {index + 1}"
                    : item.SubProfileName.Trim();
                var advance = item.Balance ?? 0m;
                var wallet = item.WalletBalance ?? 0m;

                return new StatisticsSubProfileBalanceViewModel
                {
                    Name = name,
                    AdvanceText = item.Balance.HasValue ? BalanceDisplay.FormatAmount(item.Balance) : "—",
                    WalletText = wallet > 0 ? BalanceDisplay.FormatAmount(wallet) : null,
                    DurationText = string.IsNullOrWhiteSpace(item.AdvanceDurationText)
                        ? null
                        : item.AdvanceDurationText.Trim(),
                    IsLowBalance = item.Balance is decimal balance
                        && balance > 0
                        && balance < BalanceDisplayRules.LowBalanceThresholdRub,
                    BarWidth = (double)(advance / maxAdvance)
                };
            })
            .ToList();
    }
}
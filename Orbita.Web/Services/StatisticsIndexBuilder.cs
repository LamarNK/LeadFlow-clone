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
        IOfficeContext officeContext)
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
                var subProfiles = SubProfileViewModelMapper.Map(null, a.SubProfiles);
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
                    BalanceBreakdown = SubProfileViewModelMapper.BuildBalanceBreakdown(subProfiles),
                    BalanceSubtitle = BalanceDisplay.FormatAccountBreakdown(
                        a.Wallet > 0 ? a.Wallet : null,
                        durationHint),
                    IsLowBalance = a.IsLowBalance,
                    BarWidth = (double)(a.Advance / maxBalance)
                };
            })
            .ToList();

        var summary = new StatisticsSummaryViewModel
        {
            ActiveAdsCount = data.Accounts.ActiveAdsCount,
            BlockedAdsCount = data.Accounts.BlockedAdsCount,
            PeriodTotal = data.Responses.Total,
            PeriodSent = data.Responses.Sent,
            PeriodDuplicates = data.Responses.Duplicates,
            PeriodErrors = data.Responses.Errors + data.Responses.ActionRequired,
            PeriodUniqueAuthors = data.Responses.UniqueAuthors,
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
            KpiCards = BuildKpiCards(data, period),
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

    private static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(
        OfficeStatisticsDto data,
        DashboardPeriod period)
    {
        var responsesSeries = data.DailyTrend.Select(d => d.Total).ToList();
        var duplicatesSeries = data.DailyTrend.Select(d => d.Duplicates).ToList();

        return
        [
            new()
            {
                Key = "advance",
                Href = KpiCardLinks.StatisticsCard("advance"),
                Label = "Аванс",
                Value = BalanceDisplay.FormatAmount(data.Balances.TotalAdvance),
                PreferTextValue = true,
                Delta = "Сейчас",
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-wallet",
                IconTone = "green"
            },
            new()
            {
                Key = "wallet",
                Href = KpiCardLinks.StatisticsCard("wallet"),
                Label = "Кошелёк",
                Value = BalanceDisplay.FormatAmount(data.Balances.TotalWallet),
                PreferTextValue = true,
                Delta = "Сейчас",
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-coins",
                IconTone = "blue"
            },
            new()
            {
                Key = "accounts",
                Href = KpiCardLinks.StatisticsCard("accounts"),
                Label = "Аккаунты",
                Value = $"{data.Accounts.StatusCounts.Active} / {data.Accounts.Total}",
                CountValue = data.Accounts.StatusCounts.Active,
                ValueSuffix = $" / {data.Accounts.Total}",
                Delta = data.Balances.LowBalanceAccountCount > 0
                    ? $"Низкий баланс: {data.Balances.LowBalanceAccountCount}"
                    : "Сейчас",
                DeltaTone = data.Balances.LowBalanceAccountCount > 0 ? "bad" : "good",
                IconClass = "fa-regular fa-user",
                IconTone = "purple"
            },
            new()
            {
                Key = "responses",
                Href = KpiCardLinks.StatisticsCard("responses", period.From, period.To),
                Label = "Отклики",
                Value = data.Responses.Unique.ToString(),
                CountValue = data.Responses.Unique,
                ValueSuffix = data.Responses.Duplicates > 0 ? $" / {data.Responses.Duplicates} дубл." : null,
                Delta = period.Label,
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-comments",
                IconTone = "blue",
                Sparkline = SparklineGenerator.FromSeries(responsesSeries),
                SparkColor = "#2563eb"
            },
            new()
            {
                Key = "workers",
                Href = KpiCardLinks.StatisticsCard("workers"),
                Label = "Воркеры",
                Value = $"{data.Workers.Online} / {data.Workers.Total}",
                CountValue = data.Workers.Online,
                ValueSuffix = $" / {data.Workers.Total}",
                Delta = "Онлайн",
                DeltaTone = data.Workers.Online > 0 ? "good" : "neutral",
                IconClass = "fa-solid fa-server",
                IconTone = "orange",
                Sparkline = SparklineGenerator.FromSeries(duplicatesSeries),
                SparkColor = "#16a34a"
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
}
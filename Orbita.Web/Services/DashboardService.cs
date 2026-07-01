using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class DashboardService(OrbitaApiClient api, IOptions<DesignPreviewOptions> previewOptions) : IDashboardService
{
    public async Task<DashboardViewModel> GetDashboardAsync(DashboardPeriod period, CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildDashboardViewModel(period);
        }

        var summary = await api.GetSummaryAsync(ct);
        if (summary is null)
        {
            return new DashboardViewModel
            {
                ErrorMessage = "Не удалось загрузить данные. Выйдите из панели и войдите снова."
            };
        }

        var workers = await api.GetWorkersAsync(ct) ?? [];
        var events = await api.GetEventsAsync(limit: 5, ct: ct) ?? [];
        var accountStats = await BuildAccountStatsAsync(workers, summary, ct);
        var periodStats = AggregatePeriodStats(summary, period);
        var kpiCards = BuildKpiCards(summary, periodStats, period, accountStats);
        var responseChart = BuildResponseChart(summary, period, periodStats);
        var charts = DashboardChartsBuilder.FromPresentation(
            kpiCards,
            responseChart,
            accountStats,
            periodStats.DailyPoints,
            period.IsTodayOnly);

        return new DashboardViewModel
        {
            Header = BuildHeader(period),
            KpiCards = kpiCards,
            Workers = workers.Select(w => new DashboardWorkerRowViewModel
            {
                Id = w.Id,
                DisplayName = w.DisplayName,
                MachineName = w.MachineName,
                IsOnline = w.IsOnline,
                ActiveAccounts = w.ActiveAccountCount,
                TotalAccounts = w.AccountCount,
                Responses = w.TotalToday,
                Duplicates = w.DuplicatesToday,
                Errors = w.Errors,
                LastActivityUtc = w.LastSeenAtUtc
            }).ToList(),
            HourlyChart = responseChart,
            Events = events.Select(DashboardEventMapper.Map).ToList(),
            AccountStats = accountStats,
            Charts = charts
        };
    }

    private static PageHeaderViewModel BuildHeader(DashboardPeriod period) =>
        PageHeaderBuilder.Create(
            "Панель управления",
            "Общая сводка по всем воркерам",
            showDateRange: true,
            period: period);

    private static DashboardPeriodStats AggregatePeriodStats(GlobalDashboardSummary summary, DashboardPeriod period)
    {
        var dailyPoints = summary.WeeklyByDayActivity
            .Where(p => p.LocalDate.HasValue
                && p.LocalDate.Value.Date >= period.From
                && p.LocalDate.Value.Date <= period.To)
            .OrderBy(p => p.LocalDate)
            .ToList();

        if (period.IsTodayOnly)
        {
            return new DashboardPeriodStats(
                summary.TotalToday,
                summary.Duplicates,
                summary.Errors,
                summary.HourlyActivity.Select(p => p.NewCount).ToList(),
                summary.HourlyActivity.Select(p => p.DuplicateCount).ToList(),
                summary.HourlyActivity.Select(p => p.ErrorCount).ToList(),
                dailyPoints);
        }

        if (period.IsSingleDay && dailyPoints.Count == 1)
        {
            var day = dailyPoints[0];
            return new DashboardPeriodStats(
                day.NewCount,
                day.DuplicateCount,
                day.ErrorCount,
                [day.NewCount],
                [day.DuplicateCount],
                [day.ErrorCount],
                dailyPoints);
        }

        return new DashboardPeriodStats(
            dailyPoints.Sum(p => p.NewCount),
            dailyPoints.Sum(p => p.DuplicateCount),
            dailyPoints.Sum(p => p.ErrorCount),
            dailyPoints.Select(p => p.NewCount).ToList(),
            dailyPoints.Select(p => p.DuplicateCount).ToList(),
            dailyPoints.Select(p => p.ErrorCount).ToList(),
            dailyPoints);
    }

    private static IReadOnlyList<DashboardChartPointViewModel> BuildResponseChart(
        GlobalDashboardSummary summary,
        DashboardPeriod period,
        DashboardPeriodStats periodStats)
    {
        if (period.IsTodayOnly)
        {
            return DashboardChartsBuilder.FromHourlyActivity(summary.HourlyActivity);
        }

        return DashboardChartsBuilder.FromDailyActivity(periodStats.DailyPoints);
    }

    private static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(
        GlobalDashboardSummary summary,
        DashboardPeriodStats periodStats,
        DashboardPeriod period,
        AccountStatsViewModel accountStats)
    {
        var responsesSeries = periodStats.ResponsesSeries;
        var duplicatesSeries = periodStats.DuplicatesSeries;
        var errorsSeries = periodStats.ErrorsSeries;

        return
        [
            new()
            {
                Key = "responses",
                Href = KpiCardLinks.Dashboard("responses", period.From, period.To),
                Label = "Откликов всего",
                Value = periodStats.Responses.ToString(),
                CountValue = periodStats.Responses,
                Delta = "За период",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-comments",
                IconTone = "blue",
                Sparkline = SparklineGenerator.FromSeries(responsesSeries),
                SparkColor = "#2563eb"
            },
            new()
            {
                Key = "duplicates",
                Href = KpiCardLinks.Dashboard("duplicates", period.From, period.To),
                Label = "Дублей",
                Value = periodStats.Duplicates.ToString(),
                CountValue = periodStats.Duplicates,
                Delta = "За период",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-clone",
                IconTone = "green",
                Sparkline = SparklineGenerator.FromSeries(duplicatesSeries),
                SparkColor = "#16a34a"
            },
            new()
            {
                Key = "errors",
                Href = KpiCardLinks.Dashboard("errors", period.From, period.To),
                Label = "Ошибок",
                Value = periodStats.Errors.ToString(),
                CountValue = periodStats.Errors,
                Delta = "За период",
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-triangle-exclamation",
                IconTone = "orange",
                Sparkline = SparklineGenerator.FromSeries(errorsSeries),
                SparkColor = "#f59e0b"
            },
            new()
            {
                Key = "accounts",
                Href = KpiCardLinks.Dashboard("accounts", period.From, period.To),
                Label = "Аккаунтов активно",
                Value = $"{summary.AccountStatusCounts.Active} / {summary.ConnectedAccounts}",
                CountValue = summary.AccountStatusCounts.Active,
                ValueSuffix = $" / {summary.ConnectedAccounts}",
                Delta = summary.ConnectedAccounts == 0 ? "0%" : "Сейчас",
                DeltaTone = "good",
                IconClass = "fa-regular fa-user",
                IconTone = "purple",
                SparkColor = "#7c3aed",
                Segments = DashboardChartsBuilder.BuildAccountSegments(accountStats)
            },
            new()
            {
                Key = "workers",
                Href = KpiCardLinks.Dashboard("workers", period.From, period.To),
                Label = "Воркеров онлайн",
                Value = $"{summary.OnlineWorkers} / {summary.TotalWorkers}",
                CountValue = summary.OnlineWorkers,
                ValueSuffix = $" / {summary.TotalWorkers}",
                Delta = summary.TotalWorkers == 0 ? "0%" : "Сейчас",
                DeltaTone = "good",
                IconClass = "fa-solid fa-server",
                IconTone = "blue",
                SparkColor = "#2563eb",
                Segments = DashboardChartsBuilder.BuildWorkerSegments(summary.OnlineWorkers, summary.TotalWorkers)
            }
        ];
    }

    private Task<AccountStatsViewModel> BuildAccountStatsAsync(
        IReadOnlyList<WorkerListItem> workers,
        GlobalDashboardSummary summary,
        CancellationToken ct)
    {
        // Aggregates are computed server-side in DashboardQueryService from WorkerAccounts.
        var counts = summary.AccountStatusCounts;
        return Task.FromResult(new AccountStatsViewModel
        {
            Total = summary.ConnectedAccounts,
            Active = counts.Active,
            Inactive = counts.Inactive,
            Blocked = counts.Blocked,
            Errors = counts.Errors
        });
    }

    private sealed record DashboardPeriodStats(
        int Responses,
        int Duplicates,
        int Errors,
        IReadOnlyList<int> ResponsesSeries,
        IReadOnlyList<int> DuplicatesSeries,
        IReadOnlyList<int> ErrorsSeries,
        IReadOnlyList<ActivityPointDto> DailyPoints);
}
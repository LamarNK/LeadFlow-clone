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
        var kpiCards = BuildKpiCards(summary, periodStats, period);
        var responseChart = BuildResponseChart(summary, period, periodStats);
        var charts = DashboardChartsBuilder.FromPresentation(kpiCards, responseChart, accountStats, periodStats.DailyPoints);

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
                ActiveAccounts = w.AccountCount,
                TotalAccounts = w.AccountCount,
                Responses = period.IsTodayOnly ? w.TotalToday : 0,
                Duplicates = 0,
                Errors = w.Errors,
                LastActivityUtc = w.LastSeenAtUtc
            }).ToList(),
            HourlyChart = responseChart,
            Events = events.Select(e => new DashboardEventRowViewModel
            {
                Message = e.Message,
                Subtitle = !string.IsNullOrWhiteSpace(e.Details) ? e.Details : e.AccountId.HasValue ? "Аккаунт" : string.Empty,
                TimeUtc = e.CreatedAtUtc,
                WorkerName = e.WorkerDisplayName,
                Level = e.Level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? "error"
                    : e.Level.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? "warning" : "success"
            }).ToList(),
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
        DashboardPeriod period)
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
                Sparkline = SparklineGenerator.FromHourlySeries(responsesSeries, SparklineTrend.Up),
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
                Sparkline = SparklineGenerator.FromHourlySeries(duplicatesSeries, SparklineTrend.Down),
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
                Sparkline = SparklineGenerator.FromHourlySeries(errorsSeries, SparklineTrend.UpGentle),
                SparkColor = "#f59e0b"
            },
            new()
            {
                Key = "accounts",
                Href = KpiCardLinks.Dashboard("accounts", period.From, period.To),
                Label = "Аккаунтов активно",
                Value = $"{Math.Max(0, summary.ConnectedAccounts - summary.AccountsNeedAttentionCount)} / {summary.ConnectedAccounts}",
                CountValue = Math.Max(0, summary.ConnectedAccounts - summary.AccountsNeedAttentionCount),
                ValueSuffix = $" / {summary.ConnectedAccounts}",
                Delta = summary.ConnectedAccounts == 0 ? "0%" : "Сейчас",
                DeltaTone = "good",
                IconClass = "fa-regular fa-user",
                IconTone = "purple",
                Sparkline = SparklineGenerator.FromHourlySeries(responsesSeries, SparklineTrend.Up),
                SparkColor = "#7c3aed"
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
                Sparkline = SparklineGenerator.FromHourlySeries(responsesSeries, SparklineTrend.Up),
                SparkColor = "#2563eb"
            }
        ];
    }

    private Task<AccountStatsViewModel> BuildAccountStatsAsync(
        IReadOnlyList<WorkerListItem> workers,
        GlobalDashboardSummary summary,
        CancellationToken ct)
    {
        // Optimized: use aggregates already computed server-side in DashboardQueryService
        // (from WorkerAccounts + response counts). Avoids N+1 per-worker /accounts fetches on every 10s poll.
        // The detailed per-account list remains available on Workers/Details and Accounts pages.
        var total = summary.ConnectedAccounts;
        var needAttention = summary.AccountsNeedAttentionCount;
        var active = Math.Max(0, total - needAttention);

        // Rough split for the dashboard donut; detailed classification lives in worker accounts data.
        return Task.FromResult(new AccountStatsViewModel
        {
            Total = total,
            Active = active,
            Inactive = 0, // not critical for live summary card
            Blocked = 0,
            Errors = needAttention
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
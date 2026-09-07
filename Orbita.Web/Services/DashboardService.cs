using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class DashboardService(
    OrbitaApiClient api,
    IOfficeContext officeContext,
    IOptions<DesignPreviewOptions> previewOptions) : IDashboardService
{
    public async Task<DashboardViewModel> GetDashboardAsync(
        DashboardPeriod period,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default,
        string? workerFilter = null,
        string? workerSearch = null)
    {
        pageSize = ListPageSizeDefaults.Normalize(pageSize, ListPageSizeDefaults.Dashboard);
        var tableSort = TableSort.Parse(sort, sortDir, TableSort.DashboardWorkers.Default, TableSort.DashboardWorkers.Columns);
        workerFilter = DashboardWorkerFilter.Normalize(workerFilter);

        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildDashboardViewModel(
                period,
                officeContext,
                page,
                pageSize.Value,
                tableSort.Column,
                tableSort.Dir,
                workerFilter,
                workerSearch);
        }

        var summaryTask = api.GetSummaryAsync(period.TimeZoneOffsetMinutes, period.From, period.To, ct);
        var workersTask = api.GetDashboardWorkersAsync(page, pageSize, tableSort.Column, tableSort.Dir, ct, workerFilter, workerSearch);
        var eventsTask = api.GetEventsAsync(
            limit: DashboardRecentEvents.Limit,
            sinceUtc: DashboardRecentEvents.SinceUtc,
            ct: ct);
        await Task.WhenAll(summaryTask, workersTask, eventsTask);

        var summary = await summaryTask;
        if (summary is null)
        {
            return new DashboardViewModel
            {
                ErrorMessage = "Не удалось загрузить данные. Выйдите из панели и войдите снова."
            };
        }

        var workersPage = await workersTask
            ?? new WorkersPageDto(
                [],
                0,
                1,
                pageSize.Value,
                tableSort.Column,
                tableSort.Dir,
                0,
                0);
        var workers = workersPage.Items;
        var events = await eventsTask ?? [];
        var accountStats = await BuildAccountStatsAsync(workers, summary, ct);
        var periodStats = AggregatePeriodStats(summary, period);
        var kpiCards = BuildKpiCards(summary, periodStats, period, accountStats);
        var activityChart = BuildActivityChart(summary, period, periodStats);
        var charts = DashboardChartsBuilder.FromPresentation(kpiCards, activityChart, accountStats);

        var workerRows = workers.Select(MapWorkerRow).ToList();

        return new DashboardViewModel
        {
            Header = BuildHeader(period),
            KpiCards = kpiCards,
            Workers = workerRows,
            HourlyChart = DashboardChartsBuilder.ToResponsePoints(activityChart),
            Events = events.Select(DashboardEventMapper.Map).ToList(),
            AccountStats = accountStats,
            Charts = charts,
            ShowOfficeColumn = officeContext.ShowOfficeColumn,
            EnabledWorkersCount = workersPage.EnabledCount,
            DisabledWorkersCount = workersPage.PausedCount,
            ShowWorkersMonitoringControls = workersPage.TabCounts.All > 0,
            WorkerFilter = workerFilter,
            WorkerSearchQuery = SearchQueryNormalizer.Normalize(workerSearch),
            WorkerTabCounts = workersPage.TabCounts,
            Pagination = new PaginationViewModel
            {
                Page = workersPage.Page,
                PageSize = workersPage.PageSize,
                TotalItems = workersPage.TotalCount
            },
            Sort = TableSortState.Create(workersPage.Sort, string.Equals(workersPage.Dir, "desc", StringComparison.OrdinalIgnoreCase)),
            TimeZoneOffsetMinutes = period.TimeZoneOffsetMinutes
        };
    }

    private static DashboardWorkerRowViewModel MapWorkerRow(WorkerListItem w)
    {
        var activity = WorkerActivityPresenter.Present(
            w.CurrentActivity,
            w.IsOnline,
            w.ActiveAccounts ?? w.CurrentActivity?.ActiveAccounts);
        return new DashboardWorkerRowViewModel
        {
            Id = w.Id,
            DisplayName = w.DisplayName,
            MachineName = w.MachineName,
            IpAddress = w.IpAddress,
            IsOnline = w.IsOnline,
            IsEnabled = w.IsEnabled,
            IsMonitoringPaused = w.IsMonitoringPaused,
            ActiveAccounts = w.ActiveAccountCount,
            TotalAccounts = w.AccountCount,
            LowBalanceAccountCount = w.LowBalanceAccountCount,
            Responses = w.TotalToday,
            Duplicates = w.DuplicatesToday,
            Errors = w.Errors,
            LastActivityUtc = w.LastSeenAtUtc,
            CurrentActivityLabel = activity.Label,
            CurrentActivityTone = activity.Tone,
            IsActivityLive = activity.IsLive,
            CurrentActivityPhase = activity.Phase,
            CurrentActivityNextCycleAtUtc = activity.NextCycleAtUtc,
            OfficeName = w.OfficeName
        };
    }

    private PageHeaderViewModel BuildHeader(DashboardPeriod period)
    {
        var subtitle = officeContext.ShowAllOffices
            ? "Общая сводка по всем воркерам"
            : "Сводка по выбранному офису";
        return PageHeaderBuilder.WithOfficeScope(
            PageHeaderBuilder.Create(
                "Панель управления",
                subtitle,
                showDateRange: true,
                period: period),
            officeContext);
    }

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
                summary.SentToCrm,
                summary.Duplicates,
                summary.Errors,
                summary.HourlyActivity.Select(p => p.NewCount).ToList(),
                summary.HourlyActivity.Select(p => p.SentCount).ToList(),
                summary.HourlyActivity.Select(p => p.DuplicateCount).ToList(),
                summary.HourlyActivity.Select(p => p.ErrorCount).ToList(),
                dailyPoints);
        }

        if (period.IsSingleDay && dailyPoints.Count == 1)
        {
            var day = dailyPoints[0];
            return new DashboardPeriodStats(
                day.NewCount,
                day.SentCount,
                day.DuplicateCount,
                day.ErrorCount,
                [day.NewCount],
                [day.SentCount],
                [day.DuplicateCount],
                [day.ErrorCount],
                dailyPoints);
        }

        return new DashboardPeriodStats(
            dailyPoints.Sum(p => p.NewCount),
            dailyPoints.Sum(p => p.SentCount),
            dailyPoints.Sum(p => p.DuplicateCount),
            dailyPoints.Sum(p => p.ErrorCount),
            dailyPoints.Select(p => p.NewCount).ToList(),
            dailyPoints.Select(p => p.SentCount).ToList(),
            dailyPoints.Select(p => p.DuplicateCount).ToList(),
            dailyPoints.Select(p => p.ErrorCount).ToList(),
            dailyPoints);
    }

    private static LineChartViewModel BuildActivityChart(
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
        var sentSeries = periodStats.SentSeries;
        var duplicatesSeries = periodStats.DuplicatesSeries;
        var errorsSeries = periodStats.ErrorsSeries;
        var sentShare = periodStats.Responses == 0
            ? "0%"
            : $"{periodStats.Sent * 100.0 / periodStats.Responses:0.#}%";

        return
        [
            new()
            {
                Key = "responses",
                Href = KpiCardLinks.Dashboard("responses", period.From, period.To, period.TimeZoneOffsetMinutes),
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
                Key = "sent",
                Href = KpiCardLinks.Dashboard("sent", period.From, period.To, period.TimeZoneOffsetMinutes),
                Label = "Отправленные",
                Value = periodStats.Sent.ToString(),
                CountValue = periodStats.Sent,
                Delta = sentShare,
                DeltaTone = periodStats.Sent > 0 ? "good" : "neutral",
                IconClass = "fa-solid fa-paper-plane",
                IconTone = "green",
                Sparkline = SparklineGenerator.FromSeries(sentSeries),
                SparkColor = "#15803d"
            },
            new()
            {
                Key = "duplicates",
                Href = KpiCardLinks.Dashboard("duplicates", period.From, period.To, period.TimeZoneOffsetMinutes),
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
                Href = KpiCardLinks.Dashboard("errors", period.From, period.To, period.TimeZoneOffsetMinutes),
                Label = "Ошибок",
                Value = periodStats.Errors.ToString(),
                CountValue = periodStats.Errors,
                Delta = "За период",
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-triangle-exclamation",
                IconTone = "orange",
                Sparkline = SparklineGenerator.FromSeries(errorsSeries),
                SparkColor = "#f04438"
            },
            new()
            {
                Key = "accounts",
                Href = KpiCardLinks.Dashboard("accounts", period.From, period.To, period.TimeZoneOffsetMinutes),
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
                Href = KpiCardLinks.Dashboard("workers", period.From, period.To, period.TimeZoneOffsetMinutes),
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
        int Sent,
        int Duplicates,
        int Errors,
        IReadOnlyList<int> ResponsesSeries,
        IReadOnlyList<int> SentSeries,
        IReadOnlyList<int> DuplicatesSeries,
        IReadOnlyList<int> ErrorsSeries,
        IReadOnlyList<ActivityPointDto> DailyPoints);
}

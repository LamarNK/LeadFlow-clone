using Microsoft.Extensions.Options;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class DashboardService(OrbitaApiClient api, IOptions<DesignPreviewOptions> previewOptions) : IDashboardService
{
    public async Task<DashboardViewModel> GetDashboardAsync(CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildDashboardViewModel();
        }

        var summary = await api.GetSummaryAsync(ct);
        if (summary is null)
        {
            return new DashboardViewModel { ErrorMessage = "Не удалось загрузить данные. Проверьте API." };
        }

        var workers = await api.GetWorkersAsync(ct) ?? [];
        var events = await api.GetEventsAsync(limit: 5, ct: ct) ?? [];
        var accountStats = await BuildAccountStatsAsync(workers, summary, ct);
        var kpiCards = BuildKpiCards(summary);
        var hourlyChart = DashboardChartsBuilder.FromHourlyActivity(summary.HourlyActivity);

        return new DashboardViewModel
        {
            Header = new PageHeaderViewModel
            {
                Title = "Панель управления",
                Subtitle = "Общая сводка по всем воркерам",
                ShowRefresh = true,
                ShowDateRange = true,
                UpdatedAtUtc = DateTime.UtcNow
            },
            KpiCards = kpiCards,
            Workers = workers.Select(w => new DashboardWorkerRowViewModel
            {
                Id = w.Id,
                DisplayName = w.DisplayName,
                IsOnline = w.IsOnline,
                ActiveAccounts = w.AccountCount,
                TotalAccounts = w.AccountCount,
                Responses = w.TotalToday,
                Duplicates = 0,
                Errors = w.Errors,
                LastActivityUtc = w.LastSeenAtUtc
            }).ToList(),
            HourlyChart = hourlyChart,
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
            Charts = DashboardChartsBuilder.FromPresentation(kpiCards, hourlyChart, accountStats)
        };
    }

    private static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(Orbita.Contracts.GlobalDashboardSummary s)
    {
        var hourly = s.HourlyActivity.Select(p => p.NewCount).ToList();
        if (hourly.Count < 2)
        {
            hourly = [10, 14, 12, 18, 16, 20, 22, 24];
        }

        return
        [
            new()
            {
                Label = "Откликов всего",
                Value = s.TotalToday.ToString(),
                CountValue = s.TotalToday,
                Delta = "+0%",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-comments",
                IconTone = "blue",
                Sparkline = SparklineGenerator.FromHourlySeries(hourly, SparklineTrend.Up),
                SparkColor = "#2563eb"
            },
            new()
            {
                Label = "Дублей",
                Value = s.Duplicates.ToString(),
                CountValue = s.Duplicates,
                Delta = "—",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-clone",
                IconTone = "green",
                Sparkline = SparklineGenerator.FromHourlySeries(hourly, SparklineTrend.Down),
                SparkColor = "#16a34a"
            },
            new()
            {
                Label = "Ошибок",
                Value = s.Errors.ToString(),
                CountValue = s.Errors,
                Delta = "—",
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-triangle-exclamation",
                IconTone = "orange",
                Sparkline = SparklineGenerator.FromHourlySeries(hourly, SparklineTrend.UpGentle),
                SparkColor = "#f59e0b"
            },
            new()
            {
                Label = "Аккаунтов активно",
                Value = $"{Math.Max(0, s.ConnectedAccounts - s.AccountsNeedAttentionCount)} / {s.ConnectedAccounts}",
                CountValue = Math.Max(0, s.ConnectedAccounts - s.AccountsNeedAttentionCount),
                ValueSuffix = $" / {s.ConnectedAccounts}",
                Delta = s.ConnectedAccounts == 0 ? "0%" : "100%",
                DeltaTone = "good",
                IconClass = "fa-regular fa-user",
                IconTone = "purple",
                Sparkline = SparklineGenerator.FromHourlySeries(hourly, SparklineTrend.Up),
                SparkColor = "#7c3aed"
            },
            new()
            {
                Label = "Воркеров онлайн",
                Value = $"{s.OnlineWorkers} / {s.TotalWorkers}",
                CountValue = s.OnlineWorkers,
                ValueSuffix = $" / {s.TotalWorkers}",
                Delta = s.TotalWorkers == 0 ? "0%" : $"{s.OnlineWorkers * 100 / s.TotalWorkers}%",
                DeltaTone = "good",
                IconClass = "fa-solid fa-server",
                IconTone = "blue",
                Sparkline = SparklineGenerator.FromHourlySeries(hourly, SparklineTrend.Up),
                SparkColor = "#2563eb"
            }
        ];
    }

    private async Task<AccountStatsViewModel> BuildAccountStatsAsync(
        IReadOnlyList<Orbita.Contracts.WorkerListItem> workers,
        Orbita.Contracts.GlobalDashboardSummary summary,
        CancellationToken ct)
    {
        if (workers.Count == 0)
        {
            return new AccountStatsViewModel
            {
                Total = summary.ConnectedAccounts,
                Active = Math.Max(0, summary.ConnectedAccounts - summary.AccountsNeedAttentionCount),
                Errors = summary.AccountsNeedAttentionCount
            };
        }

        var active = 0;
        var inactive = 0;
        var blocked = 0;
        var errors = 0;

        foreach (var worker in workers)
        {
            var accounts = await api.GetWorkerAccountsAsync(worker.Id, ct);
            if (accounts is null) continue;

            foreach (var account in accounts)
            {
                if (!account.IsEnabled) { inactive++; continue; }
                if (account.Status is "Blocked") { blocked++; continue; }
                if (account.Status is "RequiresLogin" or "RequiresManualAction" or "Error") errors++;
                else active++;
            }
        }

        return new AccountStatsViewModel
        {
            Total = active + inactive + blocked + errors,
            Active = active,
            Inactive = inactive,
            Blocked = blocked,
            Errors = errors
        };
    }
}
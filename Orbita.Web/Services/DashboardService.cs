using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public sealed class DashboardService(OrbitaApiClient api) : IDashboardService
{
    public async Task<DashboardViewModel> GetDashboardAsync(CancellationToken ct = default)
    {
        var summary = await api.GetSummaryAsync(ct);
        if (summary is null)
        {
            return new DashboardViewModel { ErrorMessage = "Не удалось загрузить данные. Проверьте API." };
        }

        var workers = await api.GetWorkersAsync(ct) ?? [];
        var events = await api.GetEventsAsync(limit: 10, ct: ct) ?? [];
        var accountStats = await BuildAccountStatsAsync(workers, summary, ct);

        return new DashboardViewModel
        {
            Header = new PageHeaderViewModel
            {
                Title = "Панель управления",
                Subtitle = "Общая сводка по всем воркерам",
                ShowRefresh = true,
                ShowDateRange = true,
                UpdatedAt = DateTime.Now
            },
            Summary = summary,
            Workers = workers,
            Events = events,
            AccountStats = accountStats
        };
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
                NeedAttention = summary.AccountsNeedAttentionCount
            };
        }

        var active = 0;
        var needAttention = 0;
        var inactive = 0;

        foreach (var worker in workers)
        {
            var accounts = await api.GetWorkerAccountsAsync(worker.Id, ct);
            if (accounts is null) continue;

            foreach (var account in accounts)
            {
                if (!account.IsEnabled) { inactive++; continue; }
                if (account.Status is "RequiresLogin" or "RequiresManualAction" or "Error") needAttention++;
                else active++;
            }
        }

        return new AccountStatsViewModel
        {
            Total = active + needAttention + inactive,
            Active = active,
            NeedAttention = needAttention,
            Inactive = inactive
        };
    }
}
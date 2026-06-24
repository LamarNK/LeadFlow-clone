using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class DashboardViewModel
{
    public PageHeaderViewModel Header { get; init; } = new() { Title = "Панель управления", Subtitle = "Общая сводка по всем воркерам", ShowRefresh = true, ShowDateRange = true };
    public GlobalDashboardSummary? Summary { get; init; }
    public IReadOnlyList<WorkerListItem> Workers { get; init; } = [];
    public IReadOnlyList<WorkerEventListItem> Events { get; init; } = [];
    public AccountStatsViewModel AccountStats { get; init; } = AccountStatsViewModel.Empty;
    public string? ErrorMessage { get; init; }
}

public sealed class AccountStatsViewModel
{
    public int Total { get; init; }
    public int Active { get; init; }
    public int NeedAttention { get; init; }
    public int Inactive { get; init; }

    public static AccountStatsViewModel Empty { get; } = new();
}
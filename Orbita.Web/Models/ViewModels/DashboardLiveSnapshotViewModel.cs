namespace Orbita.Web.Models.ViewModels;

public sealed class DashboardLiveSnapshotViewModel
{
    public DateTime UpdatedAtUtc { get; init; }
    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<DashboardWorkerRowViewModel> Workers { get; init; } = [];
    public IReadOnlyList<DashboardEventRowViewModel> Events { get; init; } = [];
    public AccountStatsViewModel AccountStats { get; init; } = AccountStatsViewModel.Empty;
    public DashboardChartsViewModel Charts { get; init; } = new();
    public int EnabledWorkersCount { get; init; }
    public int DisabledWorkersCount { get; init; }
    public bool ShowWorkersMonitoringControls { get; init; }
}
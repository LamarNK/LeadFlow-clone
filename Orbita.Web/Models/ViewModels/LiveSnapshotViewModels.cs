using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class ResponsesLiveSnapshotViewModel
{
    public DateTime UpdatedAtUtc { get; init; }
    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<ResponseRowViewModel> Responses { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
    public IReadOnlyList<DeliveryOfficeOptionViewModel> DeliveryOffices { get; init; } = [];
    public IReadOnlyList<SendBitrixInstanceOptionViewModel> SendBitrixInstances { get; init; } = [];
}

public sealed class WorkersLiveSnapshotViewModel
{
    public DateTime UpdatedAtUtc { get; init; }
    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<WorkerRowViewModel> Workers { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
}

public sealed class WorkerDetailsLiveSnapshotViewModel
{
    public DateTime UpdatedAtUtc { get; init; }
    public bool IsOnline { get; init; }
    public bool IsEnabled { get; init; } = true;
    public DateTime? LastActivityUtc { get; init; }
    public double? CpuPercent { get; init; }
    public double? RamPercent { get; init; }
    public long? RamUsedMb { get; init; }
    public long? RamTotalMb { get; init; }
    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<WorkerInfoItemViewModel> InfoItems { get; init; } = [];
    public IReadOnlyList<WorkerPeriodStatViewModel> PeriodStats { get; init; } = [];
    public IReadOnlyList<WorkerAccountRowViewModel> Accounts { get; init; } = [];
    public IReadOnlyList<DashboardEventRowViewModel> Events { get; init; } = [];
    public LineChartViewModel ActivityChart { get; init; } = new();
    public WorkerActivityViewModel CurrentActivity { get; init; } = new();
    public IReadOnlyList<WorkerActivityViewModel> ActiveAccountActivities { get; init; } = [];
    public IReadOnlyList<AdsPowerGroupDto> AdsPowerGroups { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> AccountGroupOptions { get; init; } = [];
    public int CatalogAccountCount { get; init; }
    public int AdsPowerAccountCount { get; init; }
    public int MultiloginAccountCount { get; init; }
    public int LocalAccountCount { get; init; }
    public WorkerBrowserProviderCheckDto? AdsPowerCheck { get; init; }
    public WorkerBrowserProviderCheckDto? MultiloginCheck { get; init; }
    public WorkerBrowserProviderCheckDto? LocalChromeCheck { get; init; }
}

public sealed class EventsLiveSnapshotViewModel
{
    public DateTime UpdatedAtUtc { get; init; }
    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<EventRowViewModel> Events { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
}

public sealed class ErrorsLiveSnapshotViewModel
{
    public DateTime UpdatedAtUtc { get; init; }
    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<ErrorRowViewModel> Errors { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
}

public sealed class AccountsLiveSnapshotViewModel
{
    public DateTime UpdatedAtUtc { get; init; }
    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<AccountRowViewModel> Accounts { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
}
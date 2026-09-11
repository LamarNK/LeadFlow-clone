using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class BalancesIndexViewModel
{
    public PageHeaderViewModel Header { get; init; } = new();
    public IReadOnlyList<BalanceSubProfileRowViewModel> Rows { get; init; } = [];
    public IReadOnlyList<TopUpSessionDto> Sessions { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
    public IReadOnlyList<EventFilterOptionViewModel> Workers { get; init; } = [];
    public IReadOnlyList<Guid> SelectedWorkerIds { get; init; } = [];
    public int TotalSubProfiles { get; init; }
    public int LowBalanceCount { get; init; }
    public int QueueCount { get; init; }
    public int AwaitingBalanceCount { get; init; }
    public int CompletedTodayCount { get; init; }
    public decimal TotalBalance { get; init; }
    public int SelectableCount { get; init; }
    public int OfflineLowBalanceCount { get; init; }
}

public sealed class BalanceSubProfileRowViewModel
{
    public Guid WorkerId { get; init; }
    public Guid AccountId { get; init; }
    public string WorkerName { get; init; } = "";
    public string AccountName { get; init; } = "";
    public string SubProfileId { get; init; } = "";
    public string SubProfileName { get; init; } = "";
    public decimal Balance { get; init; }
    public decimal RecommendedTarget { get; init; }
    public decimal RecommendedAmount { get; init; }
    public int TodayResponses { get; init; }
    public bool WorkerOnline { get; init; }
    public bool IsLowBalance { get; init; }
    public DateTime? LastUpdatedAtUtc { get; init; }
    public bool IsStale { get; init; }
    public TopUpSessionDto? Session { get; init; }
}

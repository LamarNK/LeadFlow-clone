namespace Orbita.Web.Models.ViewModels;

public sealed class ListingsIndexViewModel
{
    public PageHeaderViewModel Header { get; init; } = new();
    public IReadOnlyList<BreadcrumbItemViewModel> Breadcrumbs { get; init; } =
    [
        new() { Label = "Главная", Url = "/Dashboard" },
        new() { Label = "Объявления", IsActive = true }
    ];

    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<AccountTabViewModel> Tabs { get; init; } = [];
    public string ActiveTab { get; init; } = "active";
    public IReadOnlyList<ListingRowViewModel> Rows { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
    public string? SearchQuery { get; init; }
    public IReadOnlyList<Guid> WorkerIds { get; init; } = [];
    public IReadOnlyList<Guid> AccountIds { get; init; } = [];
    public IReadOnlyList<string> SubProfileIds { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> Workers { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> Accounts { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> SubProfiles { get; init; } = [];
    public TableSortState Sort { get; init; } = TableSortState.Create("expires", descending: false);
    public bool HasActiveFilters { get; init; }
    public IReadOnlyList<ActiveFilterChipViewModel> ActiveFilterChips { get; init; } = [];
    public int ActiveFilterCount => ActiveFilterChips.Count;
}

public sealed class ListingRowViewModel
{
    public Guid Id { get; init; }
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = string.Empty;
    public string WorkerUrl { get; init; } = string.Empty;
    public Guid AccountId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public string AccountUrl { get; init; } = string.Empty;
    public string AvitoSubProfileId { get; init; } = string.Empty;
    public string SubProfileName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string AvitoItemId { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public DateTime? PublishedAtUtc { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }
    public int? AgeDays { get; init; }
    public int? RemainingDays { get; init; }
    public string State { get; init; } = string.Empty;
    public string StateLabel { get; init; } = string.Empty;
    public string StateTone { get; init; } = "neutral";
    public string PublicationDateSource { get; init; } = string.Empty;
    public string PublicationDateSourceLabel { get; init; } = string.Empty;
    public DateTime? LastSeenAtUtc { get; init; }
    public DateTime? DetailCheckedAtUtc { get; init; }
    public bool IsActive { get; init; }
}

public sealed class ListingsLiveSnapshotViewModel
{
    public DateTime UpdatedAtUtc { get; init; }
    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<ListingRowViewModel> Rows { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
}

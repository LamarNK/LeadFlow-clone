namespace Orbita.Web.Models.ViewModels;

public sealed class EventsIndexViewModel
{
    public PageHeaderViewModel Header { get; init; } = new();
    public IReadOnlyList<BreadcrumbItemViewModel> Breadcrumbs { get; init; } =
    [
        new() { Label = "Главная", Url = "/Dashboard" },
        new() { Label = "События", IsActive = true }
    ];


    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public EventsFilterViewModel Filters { get; init; } = new();
    public IReadOnlyList<EventFilterOptionViewModel> EventTypes { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> Workers { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> Accounts { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> Levels { get; init; } = [];
    public IReadOnlyList<EventRowViewModel> Events { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
    public bool HasActiveFilters { get; init; }
    public IReadOnlyList<ActiveFilterChipViewModel> ActiveFilterChips { get; init; } = [];
    public int ActiveFilterCount => ActiveFilterChips.Count;
    public string JournalView { get; init; } = "all";
    public ErrorsIndexViewModel? ErrorsPage { get; init; }
    public TableSortState Sort { get; init; } = TableSortState.Create("time", descending: true);
}

public sealed record EventsFilterViewModel
{
    public string? Type { get; init; }
    public Guid? WorkerId { get; init; }
    public Guid? AccountId { get; init; }
    public string? Level { get; init; }
    public string? SearchQuery { get; init; }
}

public sealed class EventFilterOptionViewModel
{
    public string Value { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

public sealed class EventRowViewModel
{
    public Guid Id { get; init; }
    public DateTime OccurredAtUtc { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string EventTypeLabel { get; init; } = string.Empty;
    public string EventTypeIcon { get; init; } = "fa-regular fa-circle";
    public string EventTypeTone { get; init; } = "info";
    public string Level { get; init; } = "info";
    public string LevelLabel { get; init; } = string.Empty;
    public string? AccountName { get; init; }
    public Guid? AccountId { get; init; }
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string CopyText { get; init; } = string.Empty;
    public Guid? AttachmentId { get; init; }
}

public sealed class EventsSummaryViewModel
{
    public int Total { get; init; }
    public int Success { get; init; }
    public int Warning { get; init; }
    public int Error { get; init; }
    public int Info { get; init; }
}
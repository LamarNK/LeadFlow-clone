namespace Orbita.Web.Models.ViewModels;

public sealed class ErrorsIndexViewModel
{
    public PageHeaderViewModel Header { get; init; } = new();
    public IReadOnlyList<BreadcrumbItemViewModel> Breadcrumbs { get; init; } =
    [
        new() { Label = "Главная", Url = "/Dashboard" },
        new() { Label = "Ошибки", IsActive = true }
    ];

    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public ErrorsFilterViewModel Filters { get; init; } = new();
    public IReadOnlyList<EventFilterOptionViewModel> SeverityOptions { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> ErrorTypes { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> Workers { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> Accounts { get; init; } = [];
    public IReadOnlyList<ErrorRowViewModel> Errors { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
    public bool HasActiveFilters { get; init; }
    public IReadOnlyList<ActiveFilterChipViewModel> ActiveFilterChips { get; init; } = [];
    public int ActiveFilterCount => ActiveFilterChips.Count;
}

public sealed record ErrorsFilterViewModel
{
    public string? Severity { get; init; }
    public string? Type { get; init; }
    public Guid? WorkerId { get; init; }
    public Guid? AccountId { get; init; }
    public string? SearchQuery { get; init; }
}

public sealed class ErrorRowViewModel
{
    public Guid Id { get; init; }
    public DateTime OccurredAtUtc { get; init; }
    public string Severity { get; init; } = "medium";
    public string SeverityLabel { get; init; } = string.Empty;
    public string ErrorType { get; init; } = string.Empty;
    public string ErrorTypeLabel { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string CopyText { get; init; } = string.Empty;
    public string? AccountName { get; init; }
    public Guid? AccountId { get; init; }
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = string.Empty;
    public int OccurrenceCount { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public Guid? AttachmentId { get; init; }
}

public sealed class ErrorsSummaryViewModel
{
    public int Total { get; init; }
    public int Critical { get; init; }
    public int High { get; init; }
    public int Medium { get; init; }
    public int Low { get; init; }
}
namespace Orbita.Web.Models.ViewModels;

public sealed class AccountsIndexViewModel
{
    public PageHeaderViewModel Header { get; init; } = new();
    public IReadOnlyList<BreadcrumbItemViewModel> Breadcrumbs { get; init; } =
    [
        new() { Label = "Главная", Url = "/Dashboard" },
        new() { Label = "Аккаунты", IsActive = true }
    ];

    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<AccountTabViewModel> Tabs { get; init; } = [];
    public string ActiveTab { get; init; } = "all";
    public IReadOnlyList<AccountRowViewModel> Accounts { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
    public string? SearchQuery { get; init; }
}

public sealed class AccountTabViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

public sealed class AccountRowViewModel
{
    public Guid Id { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusTone { get; init; } = "active";
    public decimal Balance { get; init; }
    public int Responses { get; init; }
    public int UniqueResponses { get; init; }
    public int Errors { get; init; }
    public DateTime? LastActivityUtc { get; init; }
    public bool IsEnabledInPanel { get; init; } = true;
}

public sealed class AccountsSummaryViewModel
{
    public int Total { get; init; }
    public int Active { get; init; }
    public int Inactive { get; init; }
    public int Blocked { get; init; }
    public int Errors { get; init; }
}
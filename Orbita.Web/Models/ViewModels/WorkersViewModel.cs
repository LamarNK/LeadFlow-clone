using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class WorkersIndexViewModel
{
    public IReadOnlyList<BreadcrumbItemViewModel> Breadcrumbs { get; init; } =
    [
        new() { Label = "Воркеры", Url = null },
        new() { Label = "Все воркеры", IsActive = true }
    ];

    public IReadOnlyList<WorkersKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<WorkerRowViewModel> Workers { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
    public string? SearchQuery { get; init; }
    public bool HasWorkerRelease { get; init; }
    public string? LatestWorkerReleaseVersion { get; init; }
    public string? LatestWorkerDownloadUrl { get; init; }
    public IReadOnlyList<EventFilterOptionViewModel> OfficeOptions { get; init; } = [];
    public bool CanSelectOffice { get; init; }
}

public sealed class BreadcrumbItemViewModel
{
    public string Label { get; init; } = string.Empty;
    public string? Url { get; init; }
    public bool IsActive { get; init; }
}

public sealed class WorkersKpiCardViewModel
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public double CountValue { get; init; }
    public string IconClass { get; init; } = "fa-solid fa-circle";
    public string IconTone { get; init; } = "blue";
}

public sealed class WorkerRowViewModel
{
    public Guid Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public int ActiveAccounts { get; init; }
    public int TotalAccounts { get; init; }
    public int Responses { get; init; }
    public int Duplicates { get; init; }
    public int Errors { get; init; }
    public DateTime? LastActivityUtc { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? LatestReleaseVersion { get; init; }
    public string OfficeName { get; init; } = string.Empty;
}

public sealed class PaginationViewModel
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 12;
    public int TotalItems { get; init; }

    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalItems / (double)PageSize));

    public int RangeStart => TotalItems == 0 ? 0 : (Page - 1) * PageSize + 1;

    public int RangeEnd => TotalItems == 0 ? 0 : Math.Min(Page * PageSize, TotalItems);
}

public sealed class CreateWorkerResultViewModel
{
    public Guid WorkerId { get; init; }
    public string ApiKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string InstallCommand { get; init; } = string.Empty;
}

public sealed class WorkerDetailsViewModel
{
    public const string DefaultAdsPowerApiBaseUrl = "http://local.adspower.net:50325";

    public Guid WorkerId { get; init; }
    public int MaxConcurrentAccounts { get; init; } = 1;
    public string? AdsPowerApiBaseUrl { get; init; }
    public string? AdsPowerApiKey { get; init; }
    public string EffectiveAdsPowerApiBaseUrl =>
        string.IsNullOrWhiteSpace(AdsPowerApiBaseUrl) ? DefaultAdsPowerApiBaseUrl : AdsPowerApiBaseUrl;
    public double? CpuPercent { get; init; }
    public double? RamPercent { get; init; }
    public long? RamUsedMb { get; init; }
    public long? RamTotalMb { get; init; }
    public IReadOnlyList<BreadcrumbItemViewModel> Breadcrumbs { get; init; } = [];
    public string DisplayName { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public DateTime? LastActivityUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<WorkerInfoItemViewModel> InfoItems { get; init; } = [];
    public LineChartViewModel ActivityChart { get; init; } = new();
    public IReadOnlyList<DashboardEventRowViewModel> Events { get; init; } = [];
    public IReadOnlyList<WorkerPeriodStatViewModel> PeriodStats { get; init; } = [];
    public IReadOnlyList<WorkerAccountRowViewModel> Accounts { get; init; } = [];
}

public sealed class WorkerInfoItemViewModel
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public UtcTimeDisplayModel? TimeValue { get; init; }
}

public sealed class WorkerPeriodStatViewModel
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public sealed class WorkerAccountRowViewModel
{
    public Guid Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public bool IsEnabledInPanel { get; init; }
    public string AdsPowerProfileId { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusTone { get; init; } = "success";
    public string BalanceText { get; init; } = "—";
    public int Responses { get; init; }
    public DateTime? LastActivityUtc { get; init; }
    public int Errors { get; init; }
}

public sealed class WorkerExtraInfoViewModel
{
    public string IpAddress { get; init; } = "—";
    public DateTime? StartedAtUtc { get; init; }
    public string LeadFlowVersion { get; init; } = "—";
    public string AgentVersion { get; init; } = "—";
    public string OperatingSystem { get; init; } = "—";
    public string ConnectionCheck { get; init; } = "—";
}
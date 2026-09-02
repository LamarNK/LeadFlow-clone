namespace Orbita.Web.Models.ViewModels;

public sealed class DashboardViewModel
{
    public PageHeaderViewModel Header { get; init; } = new()
    {
        Title = "Панель управления",
        Subtitle = "Общая сводка по всем воркерам",
        ShowRefresh = true,
        ShowDateRange = true
    };

    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<DashboardWorkerRowViewModel> Workers { get; init; } = [];
    public IReadOnlyList<DashboardChartPointViewModel> HourlyChart { get; init; } = [];
    public IReadOnlyList<DashboardEventRowViewModel> Events { get; init; } = [];
    public AccountStatsViewModel AccountStats { get; init; } = AccountStatsViewModel.Empty;
    public DashboardChartsViewModel Charts { get; init; } = new();
    public string? ErrorMessage { get; init; }
    public bool ShowOfficeColumn { get; init; }
    public int EnabledWorkersCount { get; init; }
    public int DisabledWorkersCount { get; init; }
    public bool ShowWorkersMonitoringControls { get; init; }
    public PaginationViewModel Pagination { get; init; } = new() { PageSize = ListPageSizeDefaults.Dashboard };
    public TableSortState Sort { get; init; } = TableSortState.Create("activity", descending: true);
    public int TimeZoneOffsetMinutes { get; init; }
}

public sealed class DashboardKpiCardViewModel
{
    public string Key { get; init; } = string.Empty;
    public string? Href { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public double CountValue { get; init; }
    public string? ValueSuffix { get; init; }
    public string Delta { get; init; } = string.Empty;
    public string DeltaTone { get; init; } = "neutral";
    public string IconClass { get; init; } = "fa-solid fa-circle";
    public string IconTone { get; init; } = "blue";
    public IReadOnlyList<int> Sparkline { get; init; } = [];
    public string SparkColor { get; init; } = "#2563eb";
    public IReadOnlyList<KpiChartSegmentViewModel> Segments { get; init; } = [];
    public bool PreferTextValue { get; init; }
}

public sealed class DashboardWorkerRowViewModel
{
    public Guid Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsMonitoringPaused { get; init; }
    public int ActiveAccounts { get; init; }
    public int TotalAccounts { get; init; }
    public int Responses { get; init; }
    public int Duplicates { get; init; }
    public int Errors { get; init; }
    public DateTime? LastActivityUtc { get; init; }
    public string? CurrentActivityLabel { get; init; }
    public string CurrentActivityTone { get; init; } = "muted";
    public bool IsActivityLive { get; init; }
    public string OfficeName { get; init; } = string.Empty;
}

public sealed class DashboardChartPointViewModel
{
    public string Label { get; init; } = string.Empty;
    public int Value { get; init; }
    public bool ShowAxisLabel { get; init; }
    public int UtcHour { get; init; }
}

public sealed class DashboardEventRowViewModel
{
    public Guid Id { get; init; }
    public Guid WorkerId { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public DateTime TimeUtc { get; init; }
    public string WorkerName { get; init; } = string.Empty;
    public string Level { get; init; } = "success";
    public string LevelLabel { get; init; } = string.Empty;
    public string IconClass { get; init; } = "fa-regular fa-circle-check";
    public string IconTone { get; init; } = "success";
    public Guid? AccountId { get; init; }
    public string? AccountName { get; init; }
    public string DetailTitle { get; init; } = string.Empty;
    public string DetailSubtitle { get; init; } = string.Empty;
    public string DetailBody { get; init; } = string.Empty;
    public string CopyText { get; init; } = string.Empty;
    public Guid? AttachmentId { get; init; }
    public bool IsError { get; init; }
    public bool CanSolveCaptcha { get; init; }
    public string? CaptchaUrl { get; init; }
    public string CaptchaKind { get; init; } = "captcha";
    public string? CaptchaSubProfileId { get; init; }
}

public sealed class AccountStatsViewModel
{
    public int Total { get; init; }
    public int Active { get; init; }
    public int Inactive { get; init; }
    public int Blocked { get; init; }
    public int Errors { get; init; }

    public static AccountStatsViewModel Empty { get; } = new();
}

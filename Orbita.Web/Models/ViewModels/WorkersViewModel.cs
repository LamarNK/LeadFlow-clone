using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class WorkersIndexViewModel
{
    public PageHeaderViewModel Header { get; init; } = new();
    public IReadOnlyList<BreadcrumbItemViewModel> Breadcrumbs { get; init; } =
    [
        new() { Label = "Воркеры", Url = null },
        new() { Label = "Все воркеры", IsActive = true }
    ];

    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<WorkerRowViewModel> Workers { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
    public string? SearchQuery { get; init; }
    public string? StatusFilter { get; init; }
    public bool HasWorkerRelease { get; init; }
    public string? LatestWorkerReleaseVersion { get; init; }
    public string? LatestWorkerDownloadUrl { get; init; }
    public IReadOnlyList<EventFilterOptionViewModel> OfficeOptions { get; init; } = [];
    public bool CanSelectOffice { get; init; }
    public bool CanCreateWorker { get; init; }
    public bool HasActiveFilters { get; init; }
    public IReadOnlyList<ActiveFilterChipViewModel> ActiveFilterChips { get; init; } = [];
    public int ActiveFilterCount => ActiveFilterChips.Count;
    public TableSortState Sort { get; init; } = TableSortState.Create("name", descending: false);
    public bool ShowOfficeColumn { get; init; }
}

public sealed class BreadcrumbItemViewModel
{
    public string Label { get; init; } = string.Empty;
    public string? Url { get; init; }
    public bool IsActive { get; init; }
}

public sealed class WorkerRowViewModel
{
    public Guid Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
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
    public bool IsEnabled { get; init; } = true;
    public string? CurrentActivityLabel { get; init; }
    public string CurrentActivityTone { get; init; } = "muted";
    public bool IsActivityLive { get; init; }
    public string? CurrentActivityPhase { get; init; }
    public DateTime? CurrentActivityNextCycleAtUtc { get; init; }
}

public sealed class WorkerActivityViewModel
{
    public string Label { get; init; } = string.Empty;
    public string Tone { get; init; } = "muted";
    public bool IsLive { get; init; }
    public string? Phase { get; init; }
    public Guid? AccountId { get; init; }
    public string? SubProfileId { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public DateTime? NextCycleAtUtc { get; init; }
    public IReadOnlyList<WorkerActivityViewModel> ActiveAccounts { get; init; } = [];
}

public sealed class PaginationViewModel
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 12;
    public int TotalItems { get; init; }

    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalItems / (double)PageSize));

    public int RangeStart => TotalItems == 0 ? 0 : (Page - 1) * PageSize + 1;

    public int RangeEnd => TotalItems == 0 ? 0 : Math.Min(Page * PageSize, TotalItems);

    public IReadOnlyList<PaginationPageItemViewModel> GetVisiblePages(int windowSize = 5)
    {
        var total = TotalPages;
        if (total <= 1)
        {
            return Page > 0 && TotalItems > 0
                ? [new PaginationPageItemViewModel { Page = Page, IsCurrent = true }]
                : [];
        }

        windowSize = Math.Max(3, windowSize);
        var half = windowSize / 2;
        var start = Math.Max(1, Page - half);
        var end = Math.Min(total, start + windowSize - 1);
        start = Math.Max(1, end - windowSize + 1);

        var items = new List<PaginationPageItemViewModel>();
        if (start > 1)
        {
            items.Add(new PaginationPageItemViewModel { Page = 1 });
            if (start > 2)
            {
                items.Add(PaginationPageItemViewModel.Ellipsis);
            }
        }

        for (var p = start; p <= end; p++)
        {
            items.Add(new PaginationPageItemViewModel { Page = p, IsCurrent = p == Page });
        }

        if (end < total)
        {
            if (end < total - 1)
            {
                items.Add(PaginationPageItemViewModel.Ellipsis);
            }

            items.Add(new PaginationPageItemViewModel { Page = total });
        }

        return items;
    }
}

public sealed class PaginationPageItemViewModel
{
    public static PaginationPageItemViewModel Ellipsis { get; } = new() { IsEllipsis = true };

    public int Page { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsEllipsis { get; init; }
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
    public const string DefaultMultiloginLauncherUrl = MultiloginWorkerSettings.DefaultLauncherUrl;
    public const string DefaultMultiloginCloudApiUrl = MultiloginWorkerSettings.DefaultCloudApiUrl;

    public PageHeaderViewModel Header { get; init; } = new();
    public Guid WorkerId { get; init; }
    public int MaxConcurrentAccounts { get; init; } = 1;
    public int MaxConcurrentAccountsLimit { get; init; } = 1;
    public string? AdsPowerApiBaseUrl { get; init; }
    public string? AdsPowerApiKey { get; init; }
    public string? RuCaptchaApiKey { get; init; }
    public string? MultiloginLauncherUrl { get; init; }
    public string? MultiloginCloudApiUrl { get; init; }
    public bool HasMultiloginAutomationToken { get; init; }
    public string? LocalChromeExecutablePath { get; init; }
    public bool AdsPowerEnabled { get; init; } = true;
    public bool MultiloginEnabled { get; init; } = true;
    public bool LocalChromeEnabled { get; init; } = true;
    public WorkerBrowserProviderCheckDto AdsPowerCheck { get; init; } =
        new(WorkerBrowserProviderKinds.AdsPower, WorkerBrowserProviderStatus.Unchecked, "Не проверено");
    public WorkerBrowserProviderCheckDto MultiloginCheck { get; init; } =
        new(WorkerBrowserProviderKinds.Multilogin, WorkerBrowserProviderStatus.Unchecked, "Не проверено");
    public WorkerBrowserProviderCheckDto LocalChromeCheck { get; init; } =
        new(WorkerBrowserProviderKinds.Local, WorkerBrowserProviderStatus.Unchecked, "Не проверено");
    public string? AdsPowerGroupId { get; init; }
    public string? AdsPowerGroupName { get; init; }
    public IReadOnlyList<AdsPowerGroupDto> AdsPowerGroups { get; init; } = [];
    public bool ResponseFilterEnabled { get; init; }
    public bool ResponseFilterExcludeFemale { get; init; }
    public bool ResponseFilterExcludeMale { get; init; }
    public int? ResponseFilterMaxAgeMale { get; init; }
    public int? ResponseFilterMaxAgeFemale { get; init; }
    /// <summary>Пропускать отклики старше N дней (по дате отклика из чата Avito). null — без ограничения.</summary>
    public int? ResponseFilterMaxAgeDays { get; init; }
    public bool ResponseHighlightEnabled { get; init; }
    public string ResponseHighlightAgeBuckets { get; init; } = string.Empty;
    public string? ResponseHighlightTargetsJson { get; init; }
    public IReadOnlyList<string> ResponseHighlightBucketOptions { get; init; } = [];
    public bool AutoScheduleEnabled { get; init; }
    public string AutoScheduleDays { get; init; } = string.Empty;
    public string? AutoScheduleFromLocalTime { get; init; }
    public string? AutoScheduleToLocalTime { get; init; }
    public IReadOnlyList<string> AutoScheduleDayOptions { get; init; } = [];
    public bool MessengerAutoReplyEnabled { get; init; }
    public string? MessengerAutoReplyMessage { get; init; }
    /// <summary>Часов без смены номера до метрики «не менялся». null — default 24; 0 — выкл.</summary>
    public int? PhoneUnchangedHours { get; init; }
    public bool AutoDeliverToCrm { get; init; }
    public bool AutoDeliverToBitrix { get; init; } = true;
    public Guid OfficeId { get; init; }
    public string OfficeName { get; init; } = string.Empty;
    public string EffectiveAdsPowerApiBaseUrl =>
        string.IsNullOrWhiteSpace(AdsPowerApiBaseUrl) ? DefaultAdsPowerApiBaseUrl : AdsPowerApiBaseUrl;
    public string EffectiveMultiloginLauncherUrl =>
        string.IsNullOrWhiteSpace(MultiloginLauncherUrl) ? DefaultMultiloginLauncherUrl : MultiloginLauncherUrl;
    public string EffectiveMultiloginCloudApiUrl =>
        string.IsNullOrWhiteSpace(MultiloginCloudApiUrl) ? DefaultMultiloginCloudApiUrl : MultiloginCloudApiUrl;
    public WorkerSystemPanelViewModel System { get; init; } = new();
    public IReadOnlyList<BreadcrumbItemViewModel> Breadcrumbs { get; init; } = [];
    public string DisplayName { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public bool IsEnabled { get; init; } = true;
    public DateTime? LastActivityUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public IReadOnlyList<WorkerInfoItemViewModel> InfoItems { get; init; } = [];
    public LineChartViewModel ActivityChart { get; init; } = new();
    public IReadOnlyList<DashboardEventRowViewModel> Events { get; init; } = [];
    public IReadOnlyList<WorkerPeriodStatViewModel> PeriodStats { get; init; } = [];
    public IReadOnlyList<WorkerAccountRowViewModel> Accounts { get; init; } = [];
    public IReadOnlyList<WorkerAccountRowViewModel> HighlightAccounts { get; init; } = [];
    public int CatalogAccountCount { get; init; }
    public int AdsPowerAccountCount { get; init; }
    public int MultiloginAccountCount { get; init; }
    public int LocalAccountCount { get; init; }
    public string? AccountSearchQuery { get; init; }
    public string? AccountGroupId { get; init; }
    public string? AccountProvider { get; init; }
    public IReadOnlyList<EventFilterOptionViewModel> AccountGroupOptions { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> AccountProviderOptions { get; init; } = [];
    public bool HasActiveAccountFilters { get; init; }
    public IReadOnlyList<ActiveFilterChipViewModel> ActiveAccountFilterChips { get; init; } = [];
    public int ActiveAccountFilterCount => ActiveAccountFilterChips.Count;
    public TableSortState Sort { get; init; } = TableSortState.Create("account", descending: false);
    public WorkerActivityViewModel CurrentActivity { get; init; } = new();
    public IReadOnlyList<WorkerActivityViewModel> ActiveAccountActivities { get; init; } = [];
}

public sealed class WorkerSystemPanelViewModel
{
    public double? CpuPercent { get; init; }
    public double? RamPercent { get; init; }
    public long? RamUsedMb { get; init; }
    public long? RamTotalMb { get; init; }
    public string MachineName { get; init; } = "—";
    public string IpAddress { get; init; } = "—";
    public string OperatingSystem { get; init; } = "—";
    public string LeadFlowVersion { get; init; } = "—";
    public string AgentVersion { get; init; } = "—";
    public string ConnectionCheck { get; init; } = "—";
    public bool HasResourceMetrics => CpuPercent.HasValue || RamPercent.HasValue;
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
    public string? AdsPowerGroupId { get; init; }
    public string? AdsPowerGroupName { get; init; }
    public string? MultiloginProfileId { get; init; }
    public string? MultiloginFolderId { get; init; }
    public string? LocalUserDataDir { get; init; }
    public bool IsProviderEnabled { get; init; } = true;
    public bool IsMultilogin => !string.IsNullOrWhiteSpace(MultiloginProfileId);
    public bool IsLocal =>
        !string.IsNullOrWhiteSpace(LocalUserDataDir) && string.IsNullOrWhiteSpace(MultiloginProfileId);
    public bool IsManagedLocalProfile =>
        IsLocal && LocalChromeProfileMarkers.IsManaged(LocalUserDataDir);
    public bool NeedsFirstLogin { get; init; }
    public string OpenBrowserLabel { get; init; } = "Открыть браузер";
    public string ProfileProvider =>
        IsMultilogin ? "Multilogin" : IsLocal ? "Local" : "AdsPower";
    public string ProfileProviderLabel =>
        IsLocal ? "Обычный браузер" : ProfileProvider;
    public string ProfileProviderTone =>
        IsMultilogin ? "mlx" : IsLocal ? "local" : "ads";
    public string? LocationLabel
    {
        get
        {
            if (IsLocal)
            {
                if (IsManagedLocalProfile)
                {
                    return "Автопрофиль";
                }

                return ShortPath(LocalUserDataDir);
            }

            if (IsMultilogin)
            {
                if (string.IsNullOrWhiteSpace(MultiloginFolderId))
                {
                    return null;
                }

                var folderId = MultiloginFolderId.Trim();
                return folderId.Length <= 12 ? folderId : folderId[..8] + "…";
            }

            return string.IsNullOrWhiteSpace(AdsPowerGroupName) ? AdsPowerGroupId : AdsPowerGroupName;
        }
    }
    public string? LocationTitle
    {
        get
        {
            if (IsLocal)
            {
                if (IsManagedLocalProfile)
                {
                    return "Папка профиля создаётся автоматически на машине воркера";
                }

                return string.IsNullOrWhiteSpace(LocalUserDataDir)
                    ? null
                    : $"Папка профиля: {LocalUserDataDir}";
            }

            if (IsMultilogin)
            {
                return string.IsNullOrWhiteSpace(MultiloginFolderId)
                    ? null
                    : $"Папка Multilogin: {MultiloginFolderId}";
            }

            if (string.IsNullOrWhiteSpace(AdsPowerGroupName) && string.IsNullOrWhiteSpace(AdsPowerGroupId))
            {
                return null;
            }

            return $"Группа AdsPower: {AdsPowerGroupName ?? AdsPowerGroupId}";
        }
    }
    public string? ProfileIdTitle =>
        IsLocal
            ? (IsManagedLocalProfile || string.IsNullOrWhiteSpace(LocalUserDataDir)
                ? "Обычный браузер"
                : $"Профиль: {LocalUserDataDir}")
            : IsMultilogin
                ? (string.IsNullOrWhiteSpace(MultiloginProfileId) ? "Multilogin" : $"Профиль Multilogin: {MultiloginProfileId}")
                : (string.IsNullOrWhiteSpace(AdsPowerProfileId) ? "AdsPower" : $"Профиль AdsPower: {AdsPowerProfileId}");
    public bool HasAvitoCredentials { get; init; }
    public string? AvitoLogin { get; init; }
    public bool LocalProxyEnabled { get; init; }
    public string? LocalProxyAddress { get; init; }
    public string? LocalProxyUsername { get; init; }
    public bool HasProxyPassword { get; init; }
    public string LocalTrafficMode { get; init; } = LocalChromeTrafficRules.ModeNormal;
    public bool LocalBlockMedia { get; init; }
    public bool LocalBlockAnalytics { get; init; }
    public bool LocalBlockImages { get; init; }
    public bool LocalBlockFonts { get; init; }
    public bool LocalBlockPrefetch { get; init; }
    public int LocalNavigationTimeoutSeconds { get; init; } = LocalChromeTrafficRules.DefaultTimeoutSeconds;
    public string? LocalTrafficLastSummary { get; init; }
    public string ProxyStatus =>
        IsLocal
            ? LocalChromeProxyRules.Status(LocalProxyEnabled, LocalProxyAddress)
            : string.Empty;
    public string BrowserSessionStatus { get; init; } = LocalChromeProxyRules.BrowserFree;
    public bool CanOpenBrowser { get; init; }
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusTone { get; init; } = "success";
    public string BalanceText { get; init; } = "—";
    public decimal? Balance { get; init; }
    public int Responses { get; init; }
    public DateTime? LastActivityUtc { get; init; }
    public int Errors { get; init; }
    public string? ResponsesLink { get; init; }
    public string? ErrorsLink { get; init; }
    public string? LastErrorMessage { get; init; }
    public string? ErrorHint { get; init; }
    public IReadOnlyList<SubProfileRowViewModel> SubProfiles { get; init; } = [];
    public bool HasSubProfiles => SubProfiles.Count > 0;
    public string SubProfilesSummary { get; init; } = string.Empty;
    public bool CanRefreshSubProfiles { get; init; }
    public bool IsSubProfilesRefreshPending { get; init; }
    public bool IsProcessingNow { get; init; }
    public string? ProcessingLabel { get; init; }
    public string ProcessingTone { get; init; } = "live";
    public string? ProcessingSubProfileId { get; init; }

    private static string? ShortPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        var name = trimmed.Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        return slash >= 0 && slash < name.Length - 1 ? name[(slash + 1)..] : trimmed;
    }
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

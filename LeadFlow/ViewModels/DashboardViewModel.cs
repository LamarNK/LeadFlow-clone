using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;



using LeadFlow.Services;

using System.Collections.ObjectModel;

namespace LeadFlow.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    /// <summary>Высота столбца в UI (px); не влияет на расчёт метрик, только на визуальное масштабирование.</summary>
    private const double ChartBarMaxHeight = 128d;

    /// <summary>Меньшая шкала для недельного графика на дашборде.</summary>
    private const double WeeklyChartBarMaxHeight = 56d;

    private readonly AppRepository _repository;
    private readonly IMonitoringService _monitoringService;
    private readonly IWindowService _windowService;
    private readonly Dispatcher _uiDispatcher;

    private readonly ActivityPoint[] _hourlyLocalSlots = new ActivityPoint[24];
    private readonly ActivityPoint[] _weeklyLocalSlots = new ActivityPoint[7];
    private readonly List<CandidateResponse> _responsesDuringDashboardRefresh = new();
    private readonly DispatcherTimer _adsSearchDebounce = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly HashSet<Guid> _renewalSupportedAccountIds = [];
    private int _dashboardRefreshDepth;
    private CancellationTokenSource _viewLifetimeCts = new();

    [ObservableProperty]
    private int newResponses;

    [ObservableProperty]
    private int totalToday;

    [ObservableProperty]
    private int sentToCrm;

    [ObservableProperty]
    private int inProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuplicatesAttention))]
    private int duplicates;

    [ObservableProperty]
    private int errors;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionRequiredAttention))]
    private int actionRequired;

    [ObservableProperty]
    private int connectedAccounts;

    [ObservableProperty]
    private int requiresAuthorization;

    [ObservableProperty]
    private bool accountsNeedAttention;

    [ObservableProperty]
    private int blockedAdsCount;

    [ObservableProperty]
    private int draftsCount;

    [ObservableProperty]
    private int totalActiveViews;

    [ObservableProperty]
    private int totalActiveContacts;

    [ObservableProperty]
    private decimal totalBalance;

    [ObservableProperty]
    private bool hasBalances;

    [ObservableProperty]
    private int accountsWithBalanceCount;

    [ObservableProperty]
    private int totalSubProfileCount;

    [ObservableProperty]
    private int balanceHiddenAccountCount;

    [ObservableProperty]
    private string balanceSummaryLine = "";

    [ObservableProperty]
    private string balancePreviewHint = "";

    [ObservableProperty]
    private int lowBalanceAccountCount;

    public ObservableCollection<AccountBalanceItem> AccountBalances { get; } = new();

    /// <summary>Компактное превью на Dashboard (самые «тонкие» аккаунты первыми).</summary>
    public ObservableCollection<AccountBalanceItem> BalancePreviewAccounts { get; } = new();

    [ObservableProperty]
    private int activityChartColumns = 24;

    /// <summary>
    /// Число колонок сетки активных объявлений на главном экране (зависит от ширины блока).
    /// </summary>
    [ObservableProperty]
    private int activeAdsGridColumns = 2;

    /// <summary>Ширина карточки объявления в сетке (высота — по контенту, через Measure с InfiniteSize).</summary>
    [ObservableProperty]
    private double activeAdsTileWidth = 320;

    [ObservableProperty]
    private AdsDashboardFilter selectedAdsFilter = AdsDashboardFilter.All;

    [ObservableProperty]
    private string adsSearchQuery = "";

    [ObservableProperty]
    private AdsSortOption selectedAdsSort = AdsSortOption.ByViews;

    [ObservableProperty]
    private DashboardChartSeries selectedChartSeries = DashboardChartSeries.Responses;

    [ObservableProperty]
    private bool showStandardAdsEmpty;

    [ObservableProperty]
    private int displayedAdsCount;

    [ObservableProperty]
    private AdsScopeItem? selectedAdsScope;

    [ObservableProperty]
    private string adsScopeNotice = string.Empty;

    [ObservableProperty]
    private string adsActionNotice = string.Empty;

    [ObservableProperty]
    private bool adsActionNoticeIsError;

    /// <summary>Все объявления из снимков; фильтр/сортировка — через <see cref="DisplayedAdsView"/>.</summary>
    public ObservableCollection<DashboardAdDisplayItem> AllAdDisplayItems { get; } = new();
    public ObservableCollection<AdsScopeItem> AdsScopeItems { get; } = new();

    public ICollectionView DisplayedAdsView { get; }

    public IReadOnlyList<AdsFilterTab> AdsFilterTabs { get; } =
    [
        new AdsFilterTab(AdsDashboardFilter.All, "Все"),
        new AdsFilterTab(AdsDashboardFilter.Active, "Активные"),
        new AdsFilterTab(AdsDashboardFilter.Blocked, "С ошибками"),
        new AdsFilterTab(AdsDashboardFilter.Unpublished, "Неопубликованные"),
        new AdsFilterTab(AdsDashboardFilter.WithMessages, "С сообщениями"),
        new AdsFilterTab(AdsDashboardFilter.WithoutMessages, "Без сообщений"),
        new AdsFilterTab(AdsDashboardFilter.Drafts, "Черновики"),
        new AdsFilterTab(AdsDashboardFilter.WithIssues, "С проблемами")
    ];

    public IReadOnlyList<AdsSortChoice> AdsSortChoices { get; } =
    [
        new AdsSortChoice(AdsSortOption.ByViews, "Просмотры ↓"),
        new AdsSortChoice(AdsSortOption.ByViewsAscending, "Просмотры ↑"),
        new AdsSortChoice(AdsSortOption.ByContacts, "Сообщения ↓"),
        new AdsSortChoice(AdsSortOption.ByContactsAscending, "Сообщения ↑"),
        new AdsSortChoice(AdsSortOption.ByNewestFirst, "Новые сначала"),
        new AdsSortChoice(AdsSortOption.ByProblemsFirst, "Проблемные сначала"),
        new AdsSortChoice(AdsSortOption.ByStatus, "Статус (А→Я)"),
        new AdsSortChoice(AdsSortOption.ByDeleteDate, "Дата удаления")
    ];

    public IReadOnlyList<ChartSeriesTab> ChartSeriesTabs { get; } =
    [
        new ChartSeriesTab(DashboardChartSeries.Responses, "Отклики"),
        new ChartSeriesTab(DashboardChartSeries.Crm, "CRM"),
        new ChartSeriesTab(DashboardChartSeries.Duplicates, "Дубликаты"),
        new ChartSeriesTab(DashboardChartSeries.Errors, "Ошибки")
    ];

    public bool HasDuplicatesAttention => Duplicates > 0;

    public bool HasActionRequiredAttention => ActionRequired > 0;

    public ObservableCollection<ActivityPoint> Activity { get; } = new();
    public ObservableCollection<ActivityPoint> WeeklyActivity { get; } = new();
    public ObservableCollection<AvitoAdStatus> ActiveAds { get; } = new();
    /// <summary>
    /// Snapshot заблокированных объявлений со всех аккаунтов: id/title/city/Status (Заблокировано / Отклонено)
    /// и дата удаления. Заполняется из <see cref="IMonitoringService.GetBlockedAdsSnapshot"/> в том же
    /// обработчике <see cref="IMonitoringService.ProfileStatsUpdated"/>, что и активные.
    /// </summary>
    public ObservableCollection<AvitoAdStatus> BlockedAds { get; } = new();
    public ObservableCollection<AvitoAdStatus> UnpublishedAds { get; } = new();

    private readonly DispatcherTimer _accountPersistDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(450)
    };

    public DashboardViewModel(AppRepository repository, IMonitoringService monitoringService, IWindowService windowService)
    {
        _repository = repository;
        _monitoringService = monitoringService;
        _windowService = windowService;
        _uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        DisplayedAdsView = CollectionViewSource.GetDefaultView(AllAdDisplayItems);
        DisplayedAdsView.Filter = MatchesDisplayedAdFilter;
        _adsSearchDebounce.Tick += (_, _) =>
        {
            _adsSearchDebounce.Stop();
            RefreshDisplayedAdsView();
        };
        _monitoringService.ProfileStatsUpdated += (_, _) =>
        {
            // Один раз пришло событие — обновляем оба списка вместе, чтобы счётчик «Заблокировано» в
            // плитке и список под ней не разъезжались по содержимому.
            ApplyActiveAdsSnapshot();
            ApplyBlockedAdsSnapshot();
            ApplyUnpublishedAdsSnapshot();
        };
        repository.AccountPersisted += OnAccountPersisted;
        _accountPersistDebounce.Tick += async (_, _) =>
        {
            _accountPersistDebounce.Stop();
            try
            {
                await RefreshAsync();
            }
            catch
            {
            }
        };

        InitEmptyHourlySlots();
        InitEmptyWeeklySlots();
        RebuildAllAdDisplayItems();
    }

    partial void OnSelectedAdsFilterChanged(AdsDashboardFilter value) => RefreshDisplayedAdsView();

    partial void OnAdsSearchQueryChanged(string value)
    {
        _adsSearchDebounce.Stop();
        _adsSearchDebounce.Start();
    }

    partial void OnSelectedAdsSortChanged(AdsSortOption value) => RefreshDisplayedAdsView();

    partial void OnSelectedAdsScopeChanged(AdsScopeItem? value)
    {
        AdsScopeNotice = value?.Kind == AdsScopeKind.SubProfile &&
            !AllAdDisplayItems.Any(item => string.Equals(item.Ad.AvitoSubProfileId, value.SubProfileId, StringComparison.Ordinal))
            ? "Данные по объявлениям этого субпрофиля ещё собираются."
            : string.Empty;
        RefreshDisplayedAdsView();
    }

    partial void OnSelectedChartSeriesChanged(DashboardChartSeries value)
    {
        RebuildDisplayedActivity();
        RebuildWeeklyActivity();
    }

    [RelayCommand]
    public async Task GoToAttentionProblemsAsync()
    {
        var owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            return;
        }

        await _windowService.ShowMonitoringAsync(
            owner,
            CancellationToken.None,
            new MonitoringWindowLaunchRequest(
                FocusStatus: ResponseStatus.ActionRequired,
                SortByStatusAscending: true));
    }

    /// <summary>Пересобирает источник после обновления снимков active/blocked (не при смене вкладки фильтра).</summary>
    private void RebuildAllAdDisplayItems()
    {
        var items = new List<DashboardAdDisplayItem>(ActiveAds.Count + BlockedAds.Count + UnpublishedAds.Count);
        foreach (var ad in ActiveAds)
        {
            items.Add(new DashboardAdDisplayItem(
                DashboardAdKind.Active,
                ad,
                _renewalSupportedAccountIds.Contains(ad.AccountId)));
        }

        foreach (var ad in BlockedAds)
        {
            items.Add(new DashboardAdDisplayItem(
                DashboardAdKind.Blocked,
                ad,
                _renewalSupportedAccountIds.Contains(ad.AccountId)));
        }
        foreach (var ad in UnpublishedAds)
        {
            items.Add(new DashboardAdDisplayItem(
                DashboardAdKind.Unpublished,
                ad,
                _renewalSupportedAccountIds.Contains(ad.AccountId)));
        }

        // Нельзя менять ObservableCollection внутри DeferRefresh — CollectionView падает при старте.
        AllAdDisplayItems.Clear();
        foreach (var item in items)
        {
            AllAdDisplayItems.Add(item);
        }

        RefreshDisplayedAdsView();
    }

    /// <summary>Только фильтр/сортировка/поиск — без Clear/Add тысяч карточек и без пересоздания визуального дерева.</summary>
    private void RefreshDisplayedAdsView()
    {
        ApplyDisplayedAdsSort();
        DisplayedAdsView.Refresh();
        var count = DisplayedAdsView.Cast<object>().Count();
        DisplayedAdsCount = count;
        ShowStandardAdsEmpty = count == 0;
    }

    private bool MatchesDisplayedAdFilter(object obj)
    {
        if (obj is not DashboardAdDisplayItem item)
        {
            return false;
        }

        if (SelectedAdsScope is { Kind: AdsScopeKind.Account, AccountId: var accountId }
            && item.Ad.AccountId != accountId)
            return false;

        if (SelectedAdsScope is { Kind: AdsScopeKind.SubProfile, SubProfileId: var subProfileId }
            && !string.Equals(item.Ad.AvitoSubProfileId, subProfileId, StringComparison.Ordinal))
            return false;

        var matchesTab = SelectedAdsFilter switch
        {
            AdsDashboardFilter.All => true,
            AdsDashboardFilter.Active => item.Kind == DashboardAdKind.Active,
            AdsDashboardFilter.Blocked => item.Kind == DashboardAdKind.Blocked,
            AdsDashboardFilter.Unpublished => item.Kind == DashboardAdKind.Unpublished,
            AdsDashboardFilter.WithMessages => item.Ad.Contacts > 0,
            AdsDashboardFilter.WithoutMessages => item.Ad.Contacts == 0,
            AdsDashboardFilter.Drafts => item.Kind == DashboardAdKind.Active && IsDraftStatus(item.Ad),
            AdsDashboardFilter.WithIssues => item.Kind == DashboardAdKind.Blocked
                || (item.Kind == DashboardAdKind.Active && IsProblemActiveAd(item.Ad)),
            _ => true
        };

        if (!matchesTab)
        {
            return false;
        }

        var q = (AdsSearchQuery ?? string.Empty).Trim();
        if (q.Length == 0)
        {
            return true;
        }

        var ad = item.Ad;
        return ad.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || ad.City.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || ad.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
               || ad.Status.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private void ApplyDisplayedAdsSort()
    {
        DisplayedAdsView.SortDescriptions.Clear();

        switch (SelectedAdsSort)
        {
            case AdsSortOption.ByViews:
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.Views), ListSortDirection.Descending));
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.TitleSort), ListSortDirection.Ascending));
                break;
            case AdsSortOption.ByViewsAscending:
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.Views), ListSortDirection.Ascending));
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.TitleSort), ListSortDirection.Ascending));
                break;
            case AdsSortOption.ByContacts:
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.Contacts), ListSortDirection.Descending));
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.TitleSort), ListSortDirection.Ascending));
                break;
            case AdsSortOption.ByContactsAscending:
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.Contacts), ListSortDirection.Ascending));
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.TitleSort), ListSortDirection.Ascending));
                break;
            case AdsSortOption.ByStatus:
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.StatusSort), ListSortDirection.Ascending));
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.TitleSort), ListSortDirection.Ascending));
                break;
            case AdsSortOption.ByDeleteDate:
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.DeleteDateSortKey), ListSortDirection.Ascending));
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.TitleSort), ListSortDirection.Ascending));
                break;
            case AdsSortOption.ByNewestFirst:
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.DaysOnAvitoSort), ListSortDirection.Ascending));
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.Views), ListSortDirection.Descending));
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.TitleSort), ListSortDirection.Ascending));
                break;
            case AdsSortOption.ByProblemsFirst:
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.ProblemAttentionRankSort), ListSortDirection.Descending));
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.Contacts), ListSortDirection.Descending));
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.TitleSort), ListSortDirection.Ascending));
                break;
            default:
                DisplayedAdsView.SortDescriptions.Add(new SortDescription(nameof(DashboardAdDisplayItem.Views), ListSortDirection.Descending));
                break;
        }
    }

    private void InitEmptyHourlySlots()
    {
        for (var h = 0; h < 24; h++)
        {
            _hourlyLocalSlots[h] = NewHourSlot(h);
        }
    }

    private static ActivityPoint NewHourSlot(int hourLocal) => new()
    {
        Label = $"{hourLocal:00}:00",
        SlotStartHour = hourLocal,
        SlotSpanHours = 1
    };

    private void InitEmptyWeeklySlots()
    {
        for (var i = 0; i < 7; i++)
        {
            _weeklyLocalSlots[i] = NewEmptyWeekSlot(i);
        }
    }

    private static ActivityPoint NewEmptyWeekSlot(int indexFromWeekStart)
    {
        var d = DateTime.Today.AddDays(-6 + indexFromWeekStart);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        return new ActivityPoint
        {
            Label = d.ToString("ddd d.MM", ru),
            LocalDate = d,
            SlotStartHour = 0,
            SlotSpanHours = 1
        };
    }

    private void OnAccountPersisted(object? sender, AvitoAccount e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => OnAccountPersisted(sender, e));
            return;
        }

        _accountPersistDebounce.Stop();
        _accountPersistDebounce.Start();
    }

    /// <summary>
    /// Лёгкое обновление плиток и графиков без перечитывания JSON снимков объявлений (после настроек аккаунтов).
    /// </summary>
    public async Task RefreshSummaryAsync()
    {
        var stats = await _repository.GetDashboardStatsAsync(CancellationToken.None).ConfigureAwait(false);

        if (!_uiDispatcher.CheckAccess())
        {
            await _uiDispatcher.InvokeAsync(() => ApplyStats(stats));
        }
        else
        {
            ApplyStats(stats);
        }

        await RefreshBalancesAsync(GetViewLifetimeToken());
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        _dashboardRefreshDepth++;
        var outerRefresh = _dashboardRefreshDepth == 1;
        if (outerRefresh)
        {
            _responsesDuringDashboardRefresh.Clear();
        }

        try
        {
            var persistedAccounts = await _repository.GetAdSnapshotAccountsAsync(CancellationToken.None);
            _monitoringService.RestorePersistedAdSnapshots(persistedAccounts);

            ApplyAdsScopeItems(await _repository.GetAccountsAsync(CancellationToken.None));

            var stats = await _repository.GetDashboardStatsAsync(CancellationToken.None);
            ApplyStats(stats);

            if (outerRefresh)
            {
                var threshold = stats.AggregatedUpToUtc;
                foreach (var response in _responsesDuringDashboardRefresh
                    .GroupBy(static r => r.Id)
                    .Select(static g => g.Last()))
                {
                    var processedAt = response.ProcessedAt ?? response.CreatedAt;
                    if (processedAt > threshold)
                    {
                        ApplyProcessedResponseCore(response);
                    }
                }

                _responsesDuringDashboardRefresh.Clear();
            }
        }
        finally
        {
            _dashboardRefreshDepth--;
        }

        ApplyActiveAdsSnapshot();
        ApplyBlockedAdsSnapshot();
        ApplyUnpublishedAdsSnapshot();

        await RefreshBalancesAsync(GetViewLifetimeToken());
    }

    private void ApplyAdsScopeItems(IReadOnlyList<AvitoAccount> accounts)
    {
        var previous = SelectedAdsScope;
        _renewalSupportedAccountIds.Clear();
        foreach (var account in accounts.Where(account =>
                     account.ProfileProvider == AvitoProfileProvider.AdsPower
                     && !string.IsNullOrWhiteSpace(account.AdsPowerProfileId)
                     && !string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl)))
        {
            _renewalSupportedAccountIds.Add(account.Id);
        }

        AdsScopeItems.Clear();
        AdsScopeItems.Add(new AdsScopeItem(AdsScopeKind.All, "Все"));
        foreach (var account in accounts.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            AdsScopeItems.Add(new AdsScopeItem(AdsScopeKind.Account, account.DisplayName, account.Id));
            foreach (var sub in account.SubProfiles.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                AdsScopeItems.Add(new AdsScopeItem(AdsScopeKind.SubProfile, sub.DisplayName, account.Id, sub.Id, 1));
        }

        SelectedAdsScope = previous is null
            ? AdsScopeItems[0]
            : AdsScopeItems.FirstOrDefault(i => i.Kind == previous.Kind && i.AccountId == previous.AccountId && i.SubProfileId == previous.SubProfileId) ?? AdsScopeItems[0];
    }

    private async Task RefreshBalancesAsync(CancellationToken ct = default)
    {
        var items = await _repository.GetAccountBalancesAsync(ct);

        if (!_uiDispatcher.CheckAccess())
        {
            await _uiDispatcher.InvokeAsync(() => ApplyBalanceItems(items));
            return;
        }

        ApplyBalanceItems(items);
    }

    public void OnViewLoaded()
    {
        if (_viewLifetimeCts.IsCancellationRequested)
        {
            _viewLifetimeCts.Dispose();
            _viewLifetimeCts = new CancellationTokenSource();
        }
    }

    public void OnViewUnloaded()
    {
        if (!_viewLifetimeCts.IsCancellationRequested)
        {
            _viewLifetimeCts.Cancel();
        }
    }

    private CancellationToken GetViewLifetimeToken()
    {
        return _viewLifetimeCts.Token;
    }

    private void ApplyBalanceItems(IReadOnlyList<AccountBalanceItem> items)
    {
        AccountBalances.Clear();
        BalancePreviewAccounts.Clear();
        foreach (var item in items)
        {
            AccountBalances.Add(item);
        }

        foreach (var item in items.Take(BalanceDisplayRules.DashboardPreviewAccountLimit))
        {
            BalancePreviewAccounts.Add(item);
        }

        TotalBalance = items.Where(a => a.HasBalance).Sum(a => a.TotalBalance);
        HasBalances = items.Any(a => a.HasBalance);
        AccountsWithBalanceCount = items.Count(a => a.HasBalance);
        TotalSubProfileCount = items.Sum(a => a.SubProfileCount);
        LowBalanceAccountCount = items.Count(a => a.HasLowBalance);
        BalanceHiddenAccountCount = Math.Max(0, items.Count - BalancePreviewAccounts.Count);

        BalanceSummaryLine = items.Count > 0
            ? $"{AccountsWithBalanceCount} акк. · {TotalSubProfileCount} субпроф."
            : string.Empty;

        BalancePreviewHint = BalanceHiddenAccountCount > 0
            ? $"Показано {BalancePreviewAccounts.Count} из {items.Count} (сначала с наименьшим балансом)"
            : items.Count > 0
                ? "Все аккаунты"
                : string.Empty;
    }

    public void ApplyProcessedResponse(CandidateResponse response)
    {
        if (_dashboardRefreshDepth > 0)
        {
            _responsesDuringDashboardRefresh.Add(response);
            return;
        }

        ApplyProcessedResponseCore(response);
    }

    public void OnChartHostWidthChanged(double actualWidth)
    {
        var next = PickColumnCount(actualWidth);
        if (next == ActivityChartColumns)
        {
            return;
        }

        ActivityChartColumns = next;
        RebuildDisplayedActivity();
    }

    private static int PickColumnCount(double width) => width switch
    {
        >= 920d => 24,
        >= 760d => 12,
        >= 600d => 8,
        >= 480d => 6,
        >= 380d => 4,
        _ => 3
    };

    /// <summary>
    /// Вызывается из разметки главного экрана при изменении ширины блока «Объявления на Avito».
    /// </summary>
    public void OnAdsSectionWidthChanged(double actualWidth)
    {
        const double columnGap = 8d;
        const double minTileWidth = 260d;
        const int maxColumns = 3;

        // До 3 колонок — сколько влезает при минимальной ширине карточки (не жёсткий порог 1020px).
        var next = actualWidth <= 0
            ? 1
            : Math.Clamp(
                (int)Math.Floor((actualWidth + columnGap) / (minTileWidth + columnGap)),
                1,
                maxColumns);

        var columns = next;
        var totalGap = columnGap * Math.Max(0, columns - 1);
        var tileWidth = Math.Max(minTileWidth, (actualWidth - totalGap) / columns);
        var tileWidthChanged = Math.Abs(ActiveAdsTileWidth - tileWidth) > 0.5;
        if (tileWidthChanged)
        {
            ActiveAdsTileWidth = tileWidth;
        }

        if (next == ActiveAdsGridColumns && !tileWidthChanged)
        {
            return;
        }

        ActiveAdsGridColumns = next;
    }

    [RelayCommand]
    public async Task OpenAdAsync(object? parameter)
    {
        if (parameter is not AvitoAdStatus ad)
        {
            return;
        }

        var owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            return;
        }

        if (ad.AccountId == Guid.Empty)
        {
            return;
        }

        var account = await _repository.GetAccountByIdAsync(ad.AccountId, CancellationToken.None);
        if (account is null)
        {
            return;
        }

        await _windowService.ShowAvitoProfileAsync(owner, account, ad.Url, CancellationToken.None);
    }

    [RelayCommand]
    public async Task RenewAdAsync(DashboardAdDisplayItem? item)
    {
        if (item is null || !item.CanStartRenewal)
        {
            return;
        }

        var owner = Application.Current?.MainWindow;
        var scope = string.IsNullOrWhiteSpace(item.Ad.AvitoSubProfileName)
            ? string.Empty
            : $"\nСубпрофиль: {item.Ad.AvitoSubProfileName}";
        var confirmed = MessageBox.Show(
            owner,
            $"Опубликовать объявление «{item.Ad.Title}» на 30 дней?{scope}\n\nAvito использует доступное размещение вашего тарифа.",
            "Публикация объявления",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        item.SetRenewalRunning();
        AdsActionNoticeIsError = false;
        AdsActionNotice = $"Публикуем «{item.Ad.Title}». Не закрывайте профиль AdsPower…";

        AvitoAdRenewalResult result;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            result = await _monitoringService.RenewAdAsync(item.Ad, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            result = AvitoAdRenewalResult.Failed(
                "renewal_timeout",
                "Avito не подтвердил публикацию за 3 минуты. Проверьте объявление и повторите попытку.");
        }

        item.SetRenewalResult(result);
        AdsActionNoticeIsError = !result.Success;
        AdsActionNotice = result.Success
            ? $"«{item.Ad.Title}»: {result.Message}"
            : $"Не удалось опубликовать «{item.Ad.Title}»: {result.Message}";
    }

    private void ApplyStats(DashboardStats stats)
    {
        NewResponses = stats.NewResponses;
        TotalToday = stats.TotalToday;
        SentToCrm = stats.SentToCrm;
        InProgress = stats.InProgress;
        Duplicates = stats.Duplicates;
        Errors = stats.Errors;
        ActionRequired = stats.ActionRequired;
        ConnectedAccounts = stats.ConnectedAccounts;
        RequiresAuthorization = stats.RequiresAuthorization;
        AccountsNeedAttention = stats.AccountsNeedAttentionCount > 0;
        BlockedAdsCount = stats.BlockedAdsCount;
        DraftsCount = stats.DraftsCount;

        ReplaceHourlyFromStats(stats);
        ReplaceWeeklyFromStats(stats);
    }

    private void ReplaceHourlyFromStats(DashboardStats stats)
    {
        for (var h = 0; h < 24; h++)
        {
            var src = h < stats.HourlyActivity.Count ? stats.HourlyActivity[h] : null;
            _hourlyLocalSlots[h] = src is null
                ? NewHourSlot(h)
                : CloneHourlyPoint(src, h);
        }

        RebuildDisplayedActivity();
    }

    private void ReplaceWeeklyFromStats(DashboardStats stats)
    {
        for (var i = 0; i < 7; i++)
        {
            var src = i < stats.WeeklyByDayActivity.Count ? stats.WeeklyByDayActivity[i] : null;
            _weeklyLocalSlots[i] = src is null ? NewEmptyWeekSlot(i) : CloneWeekPoint(src);
        }

        RebuildWeeklyActivity();
    }

    private static ActivityPoint CloneWeekPoint(ActivityPoint src) => new()
    {
        Label = src.Label,
        LocalDate = src.LocalDate,
        NewCount = src.NewCount,
        SentCount = src.SentCount,
        DuplicateCount = src.DuplicateCount,
        ErrorCount = src.ErrorCount,
        SlotStartHour = src.SlotStartHour,
        SlotSpanHours = src.SlotSpanHours
    };

    private static ActivityPoint CloneHourlyPoint(ActivityPoint src, int hourLocal) => new()
    {
        Label = $"{hourLocal:00}:00",
        NewCount = src.NewCount,
        SentCount = src.SentCount,
        DuplicateCount = src.DuplicateCount,
        ErrorCount = src.ErrorCount,
        SlotStartHour = hourLocal,
        SlotSpanHours = 1
    };

    private void ApplyProcessedResponseCore(CandidateResponse response)
    {
        var createdLocal = response.CreatedAt.ToLocalTimeFromStoredUtc();
        var today = DateTime.Today;
        if (createdLocal.Date == today)
        {
            TotalToday++;
            if (response.Status == ResponseStatus.New)
            {
                NewResponses++;
            }

            switch (response.Status)
            {
                case ResponseStatus.Sent:
                    SentToCrm++;
                    break;
                case ResponseStatus.InProgress:
                    InProgress++;
                    break;
                case ResponseStatus.Duplicate:
                    Duplicates++;
                    break;
                case ResponseStatus.Error:
                    Errors++;
                    break;
                case ResponseStatus.ActionRequired:
                    ActionRequired++;
                    break;
            }

            var hour = Math.Clamp(createdLocal.Hour, 0, 23);
            var bucket = _hourlyLocalSlots[hour];
            bucket.NewCount++;
            switch (response.Status)
            {
                case ResponseStatus.Sent:
                    bucket.SentCount++;
                    break;
                case ResponseStatus.Duplicate:
                    bucket.DuplicateCount++;
                    break;
                case ResponseStatus.Error:
                    bucket.ErrorCount++;
                    break;
            }

            _hourlyLocalSlots[hour] = new ActivityPoint
            {
                Label = bucket.Label,
                NewCount = bucket.NewCount,
                SentCount = bucket.SentCount,
                DuplicateCount = bucket.DuplicateCount,
                ErrorCount = bucket.ErrorCount,
                SlotStartHour = bucket.SlotStartHour,
                SlotSpanHours = bucket.SlotSpanHours
            };

            RebuildDisplayedActivity();
        }

        BumpWeeklyLocalSlot(response, createdLocal);
        RebuildWeeklyActivity();
    }

    private void RebuildDisplayedActivity()
    {
        var columns = ActivityChartColumns;
        if (columns <= 0 || 24 % columns != 0)
        {
            columns = 24;
        }

        var hoursPerSlot = 24 / columns;
        var series = SelectedChartSeries;
        var maxMetric = 0;
        for (var h = 0; h < 24; h++)
        {
            var p = _hourlyLocalSlots[h];
            maxMetric = Math.Max(maxMetric, GetMetricForSeries(p, series));
        }

        var fillHex = HexForSeries(series);
        Activity.Clear();
        for (var slot = 0; slot < columns; slot++)
        {
            var startHour = slot * hoursPerSlot;
            var endHour = startHour + hoursPerSlot - 1;
            var merged = new ActivityPoint
            {
                SlotStartHour = startHour,
                SlotSpanHours = hoursPerSlot,
                Label = FormatSlotLabel(startHour, endHour, hoursPerSlot),
                NewCount = 0,
                SentCount = 0,
                DuplicateCount = 0,
                ErrorCount = 0
            };

            for (var h = startHour; h <= endHour; h++)
            {
                var p = _hourlyLocalSlots[h];
                merged.NewCount += p.NewCount;
                merged.SentCount += p.SentCount;
                merged.DuplicateCount += p.DuplicateCount;
                merged.ErrorCount += p.ErrorCount;
            }

            var slotMetric = GetMetricForSeries(merged, series);
            merged.ChartDisplayValue = slotMetric;
            merged.ChartBarFillHex = fillHex;
            merged.ChartBarHeight = maxMetric > 0
                ? Math.Min(ChartBarMaxHeight, ChartBarMaxHeight * slotMetric / (double)maxMetric)
                : 0d;
            merged.ChartTooltip = BuildActivityTooltip(merged);
            Activity.Add(merged);
        }
    }

    private static string FormatSlotLabel(int startHour, int endHour, int hoursPerSlot) =>
        hoursPerSlot <= 1
            ? $"{startHour:00}:00"
            : $"{startHour:00}:00–{endHour:00}:59";

    private void BumpWeeklyLocalSlot(CandidateResponse response, DateTime createdLocal)
    {
        var weekStart = DateTime.Today.AddDays(-6);
        if (createdLocal.Date < weekStart || createdLocal.Date > DateTime.Today)
        {
            return;
        }

        for (var i = 0; i < 7; i++)
        {
            var slot = _weeklyLocalSlots[i];
            if (slot.LocalDate != createdLocal.Date)
            {
                continue;
            }

            slot.NewCount++;
            switch (response.Status)
            {
                case ResponseStatus.Sent:
                    slot.SentCount++;
                    break;
                case ResponseStatus.Duplicate:
                    slot.DuplicateCount++;
                    break;
                case ResponseStatus.Error:
                    slot.ErrorCount++;
                    break;
            }

            return;
        }
    }

    private void RebuildWeeklyActivity()
    {
        var series = SelectedChartSeries;
        var maxMetric = 0;
        for (var i = 0; i < 7; i++)
        {
            var p = _weeklyLocalSlots[i];
            maxMetric = Math.Max(maxMetric, GetMetricForSeries(p, series));
        }

        var fillHex = HexForSeries(series);
        WeeklyActivity.Clear();
        for (var i = 0; i < 7; i++)
        {
            var src = _weeklyLocalSlots[i];
            var slotMetric = GetMetricForSeries(src, series);
            var merged = new ActivityPoint
            {
                Label = src.Label,
                LocalDate = src.LocalDate,
                NewCount = src.NewCount,
                SentCount = src.SentCount,
                DuplicateCount = src.DuplicateCount,
                ErrorCount = src.ErrorCount,
                SlotStartHour = src.SlotStartHour,
                SlotSpanHours = src.SlotSpanHours,
                ChartDisplayValue = slotMetric,
                ChartBarFillHex = fillHex,
                ChartBarHeight = maxMetric > 0
                    ? Math.Min(WeeklyChartBarMaxHeight, WeeklyChartBarMaxHeight * slotMetric / (double)maxMetric)
                    : 0d,
                ChartTooltip = BuildWeekActivityTooltip(src)
            };
            WeeklyActivity.Add(merged);
        }
    }

    private static int GetMetricForSeries(ActivityPoint p, DashboardChartSeries series) =>
        series switch
        {
            DashboardChartSeries.Crm => p.SentCount,
            DashboardChartSeries.Duplicates => p.DuplicateCount,
            DashboardChartSeries.Errors => p.ErrorCount,
            _ => p.NewCount
        };

    private static string HexForSeries(DashboardChartSeries series) =>
        series switch
        {
            DashboardChartSeries.Crm => "#16A34A",
            DashboardChartSeries.Duplicates => "#F97316",
            DashboardChartSeries.Errors => "#EF4444",
            _ => "#2563EB"
        };

    private static bool IsDraftStatus(AvitoAdStatus ad) =>
        ad.Status.Contains("черновик", StringComparison.CurrentCultureIgnoreCase)
        || ad.Status.Contains("draft", StringComparison.CurrentCultureIgnoreCase);

    private static bool IsProblemActiveAd(AvitoAdStatus ad) =>
        IsDraftStatus(ad)
        || ad.Status.Contains("отклон", StringComparison.CurrentCultureIgnoreCase)
        || ad.Status.Contains("блок", StringComparison.CurrentCultureIgnoreCase)
        || ad.Status.Contains("модерац", StringComparison.CurrentCultureIgnoreCase)
        || ad.Status.Contains("наруш", StringComparison.CurrentCultureIgnoreCase)
        || ad.Status.Contains("действ", StringComparison.CurrentCultureIgnoreCase)
        || ad.Status.Contains("требу", StringComparison.CurrentCultureIgnoreCase)
        || ad.Status.Contains("истёк", StringComparison.CurrentCultureIgnoreCase)
        || ad.Status.Contains("истек", StringComparison.CurrentCultureIgnoreCase);

    private static int ProblemAttentionRank(DashboardAdKind kind, AvitoAdStatus ad)
    {
        if (kind == DashboardAdKind.Blocked)
        {
            return 400;
        }

        if (IsDraftStatus(ad))
        {
            return 300;
        }

        if (ad.Status.Contains("отклон", StringComparison.CurrentCultureIgnoreCase)
            || ad.Status.Contains("модерац", StringComparison.CurrentCultureIgnoreCase)
            || ad.Status.Contains("наруш", StringComparison.CurrentCultureIgnoreCase)
            || ad.Status.Contains("действ", StringComparison.CurrentCultureIgnoreCase)
            || ad.Status.Contains("требу", StringComparison.CurrentCultureIgnoreCase)
            || ad.Status.Contains("истёк", StringComparison.CurrentCultureIgnoreCase)
            || ad.Status.Contains("истек", StringComparison.CurrentCultureIgnoreCase))
        {
            return 200;
        }

        if (ad.Contacts > 0)
        {
            return 50;
        }

        return 0;
    }

    private static string BuildWeekActivityTooltip(ActivityPoint day)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var dateText = day.LocalDate is DateTime d
            ? d.ToString("dddd d MMMM yyyy", ru)
            : day.Label;

        return FormattableString.Invariant(
            $"{dateText}\nВсего: {day.NewCount}\nВ CRM: {day.SentCount}\nДубли: {day.DuplicateCount}\nОшибки: {day.ErrorCount}");
    }

    private static string BuildActivityTooltip(ActivityPoint slot)
    {
        var endHour = slot.SlotStartHour + slot.SlotSpanHours - 1;
        var span = slot.SlotSpanHours <= 1
            ? $"{slot.SlotStartHour:00}:00"
            : $"{slot.SlotStartHour:00}:00–{endHour:00}:59";

        return FormattableString.Invariant(
            $"{span}\nВсего: {slot.NewCount}\nВ CRM: {slot.SentCount}\nДубли: {slot.DuplicateCount}\nОшибки: {slot.ErrorCount}");
    }

    private void ApplyActiveAdsSnapshot()
    {
        var snapshot = _monitoringService.GetActiveAdsSnapshot()
            .OrderByDescending(static ad => ad.Views)
            .ThenBy(static ad => ad.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        void UpdateCollection()
        {
            for (var index = 0; index < snapshot.Count; index++)
            {
                var desired = snapshot[index];
                if (index < ActiveAds.Count
                    && ActiveAds[index].AccountId == desired.AccountId
                    && string.Equals(ActiveAds[index].Id, desired.Id, StringComparison.Ordinal))
                {
                    if (!AreEquivalent(ActiveAds[index], desired))
                    {
                        ActiveAds[index] = CloneAd(desired);
                    }

                    continue;
                }

                var existingIndex = FindAdIndex(desired.AccountId, desired.Id, index + 1);
                if (existingIndex >= 0)
                {
                    ActiveAds.Move(existingIndex, index);
                    if (!AreEquivalent(ActiveAds[index], desired))
                    {
                        ActiveAds[index] = CloneAd(desired);
                    }

                    continue;
                }

                ActiveAds.Insert(index, CloneAd(desired));
            }

            while (ActiveAds.Count > snapshot.Count)
            {
                ActiveAds.RemoveAt(ActiveAds.Count - 1);
            }

            TotalActiveViews = snapshot.Sum(static ad => ad.Views);
            TotalActiveContacts = snapshot.Sum(static ad => ad.Contacts);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() =>
            {
                UpdateCollection();
                RebuildAllAdDisplayItems();
            });
            return;
        }

        UpdateCollection();
        RebuildAllAdDisplayItems();
    }

    private int FindAdIndex(Guid accountId, string id, int startIndex)
    {
        for (var index = startIndex; index < ActiveAds.Count; index++)
        {
            if (ActiveAds[index].AccountId == accountId
                && string.Equals(ActiveAds[index].Id, id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Аналог <see cref="ApplyActiveAdsSnapshot"/> для заблокированных. Отдельный список нужен,
    /// чтобы пользователь сразу видел: какие именно объявления улетели в «С ошибками» и в какую дату
    /// будут удалены навсегда (Avito удаляет блок-ы через ~30 дней).
    /// </summary>
    private void ApplyBlockedAdsSnapshot()
    {
        // Сортируем «свежие к удалению» первыми: чем меньше осталось дней, тем выше приоритет внимания.
        // DeleteDate — свободный текст («удалится навсегда 22 мая в 20:50»), сравниваем по нему лексикографически
        // только в пределах одного снапшота — для UI этого достаточно, точная дата не нужна.
        var snapshot = _monitoringService.GetBlockedAdsSnapshot()
            .OrderBy(static ad => ad.DeleteDate, StringComparer.Ordinal)
            .ThenBy(static ad => ad.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        void UpdateCollection()
        {
            for (var index = 0; index < snapshot.Count; index++)
            {
                var desired = snapshot[index];
                if (index < BlockedAds.Count
                    && BlockedAds[index].AccountId == desired.AccountId
                    && string.Equals(BlockedAds[index].Id, desired.Id, StringComparison.Ordinal))
                {
                    if (!AreEquivalent(BlockedAds[index], desired))
                    {
                        BlockedAds[index] = CloneAd(desired);
                    }

                    continue;
                }

                var existingIndex = FindBlockedAdIndex(desired.AccountId, desired.Id, index + 1);
                if (existingIndex >= 0)
                {
                    BlockedAds.Move(existingIndex, index);
                    if (!AreEquivalent(BlockedAds[index], desired))
                    {
                        BlockedAds[index] = CloneAd(desired);
                    }

                    continue;
                }

                BlockedAds.Insert(index, CloneAd(desired));
            }

            while (BlockedAds.Count > snapshot.Count)
            {
                BlockedAds.RemoveAt(BlockedAds.Count - 1);
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() =>
            {
                UpdateCollection();
                RebuildAllAdDisplayItems();
            });
            return;
        }

        UpdateCollection();
        RebuildAllAdDisplayItems();
    }

    private int FindBlockedAdIndex(Guid accountId, string id, int startIndex)
    {
        for (var index = startIndex; index < BlockedAds.Count; index++)
        {
            if (BlockedAds[index].AccountId == accountId
                && string.Equals(BlockedAds[index].Id, id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private void ApplyUnpublishedAdsSnapshot()
    {
        var snapshot = _monitoringService.GetUnpublishedAdsSnapshot()
            .OrderBy(static ad => ad.Status, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static ad => ad.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        void Update()
        {
            UnpublishedAds.Clear();
            foreach (var ad in snapshot) UnpublishedAds.Add(CloneAd(ad));
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => { Update(); RebuildAllAdDisplayItems(); });
            return;
        }
        Update();
        RebuildAllAdDisplayItems();
    }

    private static bool AreEquivalent(AvitoAdStatus left, AvitoAdStatus right) =>
        left.AccountId == right.AccountId
        && string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && string.Equals(left.City, right.City, StringComparison.Ordinal)
        && string.Equals(left.AddressText, right.AddressText, StringComparison.Ordinal)
        && string.Equals(left.DistrictText, right.DistrictText, StringComparison.Ordinal)
        && string.Equals(left.Salary, right.Salary, StringComparison.Ordinal)
        && left.Views == right.Views
        && left.Contacts == right.Contacts
        && left.Favorites == right.Favorites
        && string.Equals(left.Status, right.Status, StringComparison.Ordinal)
        && string.Equals(left.SourceTab, right.SourceTab, StringComparison.Ordinal)
        && string.Equals(left.ErrorReason, right.ErrorReason, StringComparison.Ordinal)
        && left.CanPublish == right.CanPublish
        && string.Equals(left.DeleteDate, right.DeleteDate, StringComparison.Ordinal)
        && left.DaysOnAvito == right.DaysOnAvito
        && left.HasDaysOnAvito == right.HasDaysOnAvito
        && left.ExpiresAtUtc == right.ExpiresAtUtc
        && left.RemainingDays == right.RemainingDays
        && string.Equals(left.ExpiryParseError, right.ExpiryParseError, StringComparison.Ordinal)
        && string.Equals(left.UrlParseError, right.UrlParseError, StringComparison.Ordinal)
        && string.Equals(left.Url, right.Url, StringComparison.Ordinal);

    private static AvitoAdStatus CloneAd(AvitoAdStatus ad) => new()
    {
        AccountId = ad.AccountId,
        AvitoSubProfileId = ad.AvitoSubProfileId,
        AvitoSubProfileName = ad.AvitoSubProfileName,
        Id = ad.Id,
        Title = ad.Title,
        City = ad.City,
        AddressText = ad.AddressText,
        DistrictText = ad.DistrictText,
        Salary = ad.Salary,
        Views = ad.Views,
        Contacts = ad.Contacts,
        Favorites = ad.Favorites,
        Status = ad.Status,
        SourceTab = ad.SourceTab,
        ErrorReason = ad.ErrorReason,
        CanPublish = ad.CanPublish,
        DeleteDate = ad.DeleteDate,
        DaysOnAvito = ad.DaysOnAvito,
        HasDaysOnAvito = ad.HasDaysOnAvito,
        ExpiresAtUtc = ad.ExpiresAtUtc,
        RemainingDays = ad.RemainingDays,
        ExpiryParseError = ad.ExpiryParseError,
        UrlParseError = ad.UrlParseError,
        Url = ad.Url
    };

    [RelayCommand]
    private async Task OpenBalanceDetailsAsync()
    {
        var owner = Application.Current?.MainWindow;
        if (owner is null) return;

        await _windowService.ShowBalanceDetailsAsync(owner, CancellationToken.None);
    }
}

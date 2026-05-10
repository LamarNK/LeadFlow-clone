using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow;
using LeadFlow.Data;
using LeadFlow.Models;
using LeadFlow.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

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

    private readonly ActivityPoint[] _hourlyLocalSlots = new ActivityPoint[24];
    private readonly ActivityPoint[] _weeklyLocalSlots = new ActivityPoint[7];
    private readonly List<CandidateResponse> _responsesDuringDashboardRefresh = new();
    private int _dashboardRefreshDepth;

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
    private int activityChartColumns = 24;

    /// <summary>
    /// Число колонок сетки активных объявлений на главном экране (зависит от ширины блока).
    /// </summary>
    [ObservableProperty]
    private int activeAdsGridColumns = 2;

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

    /// <summary>Единая сетка объявлений с учётом фильтра, поиска и сортировки.</summary>
    public ObservableCollection<DashboardAdDisplayItem> DisplayedAds { get; } = new();

    public IReadOnlyList<AdsFilterTab> AdsFilterTabs { get; } =
    [
        new AdsFilterTab(AdsDashboardFilter.All, "Все"),
        new AdsFilterTab(AdsDashboardFilter.Active, "Активные"),
        new AdsFilterTab(AdsDashboardFilter.Blocked, "Заблокированные"),
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

    public int DisplayedAdsCount => DisplayedAds.Count;

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

    private readonly DispatcherTimer _accountPersistDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(450)
    };

    public DashboardViewModel(AppRepository repository, IMonitoringService monitoringService, IWindowService windowService)
    {
        _repository = repository;
        _monitoringService = monitoringService;
        _windowService = windowService;
        _monitoringService.ProfileStatsUpdated += (_, _) =>
        {
            // Один раз пришло событие — обновляем оба списка вместе, чтобы счётчик «Заблокировано» в
            // плитке и список под ней не разъезжались по содержимому.
            ApplyActiveAdsSnapshot();
            ApplyBlockedAdsSnapshot();
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
        RebuildDisplayedAds();
    }

    partial void OnSelectedAdsFilterChanged(AdsDashboardFilter value) => RebuildDisplayedAds();

    partial void OnAdsSearchQueryChanged(string value) => RebuildDisplayedAds();

    partial void OnSelectedAdsSortChanged(AdsSortOption value) => RebuildDisplayedAds();

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

    /// <summary>Пересобирает <see cref="DisplayedAds"/> после смены фильтра, поиска или сортировки.</summary>
    public void RebuildDisplayedAds()
    {
        var rows = new List<(AvitoAdStatus ad, DashboardAdKind kind)>(ActiveAds.Count + BlockedAds.Count);

        switch (SelectedAdsFilter)
        {
            case AdsDashboardFilter.All:
                foreach (var ad in ActiveAds)
                {
                    rows.Add((ad, DashboardAdKind.Active));
                }

                foreach (var ad in BlockedAds)
                {
                    rows.Add((ad, DashboardAdKind.Blocked));
                }

                break;
            case AdsDashboardFilter.Active:
                foreach (var ad in ActiveAds)
                {
                    rows.Add((ad, DashboardAdKind.Active));
                }

                break;
            case AdsDashboardFilter.Blocked:
                foreach (var ad in BlockedAds)
                {
                    rows.Add((ad, DashboardAdKind.Blocked));
                }

                break;
            case AdsDashboardFilter.WithMessages:
                foreach (var ad in ActiveAds.Where(static a => a.Contacts > 0))
                {
                    rows.Add((ad, DashboardAdKind.Active));
                }

                foreach (var ad in BlockedAds.Where(static a => a.Contacts > 0))
                {
                    rows.Add((ad, DashboardAdKind.Blocked));
                }

                break;
            case AdsDashboardFilter.WithoutMessages:
                foreach (var ad in ActiveAds.Where(static a => a.Contacts == 0))
                {
                    rows.Add((ad, DashboardAdKind.Active));
                }

                foreach (var ad in BlockedAds.Where(static a => a.Contacts == 0))
                {
                    rows.Add((ad, DashboardAdKind.Blocked));
                }

                break;
            case AdsDashboardFilter.Drafts:
                foreach (var ad in ActiveAds.Where(static a => IsDraftStatus(a)))
                {
                    rows.Add((ad, DashboardAdKind.Active));
                }

                break;
            case AdsDashboardFilter.WithIssues:
                foreach (var ad in ActiveAds.Where(static a => IsProblemActiveAd(a)))
                {
                    rows.Add((ad, DashboardAdKind.Active));
                }

                foreach (var ad in BlockedAds)
                {
                    rows.Add((ad, DashboardAdKind.Blocked));
                }

                break;
        }

        var q = (AdsSearchQuery ?? string.Empty).Trim();
        if (q.Length > 0)
        {
            rows = rows
                .Where(t =>
                    t.ad.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || t.ad.City.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || t.ad.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || t.ad.Status.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        }

        IEnumerable<(AvitoAdStatus ad, DashboardAdKind kind)> ordered = SelectedAdsSort switch
        {
            AdsSortOption.ByViews => rows
                .OrderByDescending(t => t.ad.Views)
                .ThenBy(t => t.ad.Title, StringComparer.CurrentCultureIgnoreCase),
            AdsSortOption.ByViewsAscending => rows
                .OrderBy(t => t.ad.Views)
                .ThenBy(t => t.ad.Title, StringComparer.CurrentCultureIgnoreCase),
            AdsSortOption.ByContacts => rows
                .OrderByDescending(t => t.ad.Contacts)
                .ThenBy(t => t.ad.Title, StringComparer.CurrentCultureIgnoreCase),
            AdsSortOption.ByContactsAscending => rows
                .OrderBy(t => t.ad.Contacts)
                .ThenBy(t => t.ad.Title, StringComparer.CurrentCultureIgnoreCase),
            AdsSortOption.ByStatus => rows
                .OrderBy(t => t.ad.Status, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.ad.Title, StringComparer.CurrentCultureIgnoreCase),
            AdsSortOption.ByDeleteDate => rows
                .OrderBy(t => string.IsNullOrEmpty(t.ad.DeleteDate) ? "\uFFFF" : t.ad.DeleteDate, StringComparer.Ordinal)
                .ThenBy(t => t.ad.Title, StringComparer.CurrentCultureIgnoreCase),
            AdsSortOption.ByNewestFirst => rows
                .OrderBy(t => t.ad.DaysOnAvito)
                .ThenByDescending(t => t.ad.Views)
                .ThenBy(t => t.ad.Title, StringComparer.CurrentCultureIgnoreCase),
            AdsSortOption.ByProblemsFirst => rows
                .OrderByDescending(t => ProblemAttentionRank(t.kind, t.ad))
                .ThenByDescending(t => t.ad.Contacts)
                .ThenBy(t => t.ad.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => rows.OrderByDescending(t => t.ad.Views)
        };

        DisplayedAds.Clear();
        foreach (var row in ordered)
        {
            DisplayedAds.Add(new DashboardAdDisplayItem(row.kind, row.ad));
        }

        ShowStandardAdsEmpty = DisplayedAds.Count == 0;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(DisplayedAdsCount)));
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
            var persistedAccounts = await _repository.GetAccountsAsync(CancellationToken.None);
            _monitoringService.RestorePersistedAdSnapshots(persistedAccounts);

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
        // Меньше колонок при той же ширине — карточки крупнее и читабельнее.
        var next = actualWidth switch
        {
            >= 1020d => 3,
            >= 640d => 2,
            _ => 1
        };

        if (next == ActiveAdsGridColumns)
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

        var account = (await _repository.GetAccountsAsync(CancellationToken.None))
            .FirstOrDefault(x => x.Id == ad.AccountId);
        if (account is null)
        {
            return;
        }

        await _windowService.ShowAvitoProfileAsync(owner, account, ad.Url, CancellationToken.None);
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
                RebuildDisplayedAds();
            });
            return;
        }

        UpdateCollection();
        RebuildDisplayedAds();
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
                RebuildDisplayedAds();
            });
            return;
        }

        UpdateCollection();
        RebuildDisplayedAds();
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

    private static bool AreEquivalent(AvitoAdStatus left, AvitoAdStatus right) =>
        left.AccountId == right.AccountId
        && string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && string.Equals(left.City, right.City, StringComparison.Ordinal)
        && string.Equals(left.Salary, right.Salary, StringComparison.Ordinal)
        && left.Views == right.Views
        && left.Contacts == right.Contacts
        && left.Favorites == right.Favorites
        && string.Equals(left.Status, right.Status, StringComparison.Ordinal)
        && string.Equals(left.DeleteDate, right.DeleteDate, StringComparison.Ordinal)
        && left.DaysOnAvito == right.DaysOnAvito
        && string.Equals(left.Url, right.Url, StringComparison.Ordinal);

    private static AvitoAdStatus CloneAd(AvitoAdStatus ad) => new()
    {
        AccountId = ad.AccountId,
        Id = ad.Id,
        Title = ad.Title,
        City = ad.City,
        Salary = ad.Salary,
        Views = ad.Views,
        Contacts = ad.Contacts,
        Favorites = ad.Favorites,
        Status = ad.Status,
        DeleteDate = ad.DeleteDate,
        DaysOnAvito = ad.DaysOnAvito,
        Url = ad.Url
    };
}

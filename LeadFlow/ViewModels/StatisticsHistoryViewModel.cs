using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Data;
using LeadFlow.Models;

namespace LeadFlow.ViewModels;

public partial class StatisticsHistoryViewModel(AppRepository repository) : ObservableObject
{
    private const double StackedChartMaxHeight = 120d;
    private const double TrendChartHeight = 110d;

    private readonly AppRepository _repository = repository;

    public ObservableCollection<DailyResponseStatsRow> DailyRows { get; } = [];

    [ObservableProperty]
    private DateTime? periodStartDate = DateTime.Today.AddDays(-13);

    [ObservableProperty]
    private DateTime? periodEndDate = DateTime.Today;

    [ObservableProperty]
    private bool hasData;

    [ObservableProperty]
    private int periodTotal;

    [ObservableProperty]
    private int periodSent;

    [ObservableProperty]
    private int periodInProgress;

    [ObservableProperty]
    private int periodActionRequired;

    [ObservableProperty]
    private int periodDuplicates;

    [ObservableProperty]
    private int periodErrors;

    [ObservableProperty]
    private string periodRangeText = string.Empty;

    [ObservableProperty]
    private PointCollection totalTrendPoints = [];

    [ObservableProperty]
    private double trendCanvasWidth = 480d;

    [RelayCommand]
    private async Task ApplyQuickRangeAsync(object? parameter)
    {
        var days = ParseQuickDays(parameter);
        days = Math.Clamp(days, 1, 366);
        PeriodEndDate = DateTime.Today;
        PeriodStartDate = DateTime.Today.AddDays(-(days - 1));
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var (start, end) = NormalizeAndSyncPeriodDates();
        var buckets = await _repository.GetDailyResponseStatsForLocalRangeAsync(start, end, CancellationToken.None);
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var maxTotal = buckets.Count == 0 ? 0 : buckets.Max(b => b.Total);

        DailyRows.Clear();
        foreach (var b in buckets)
        {
            var isToday = b.DateLocal.Date == DateTime.Today;
            DailyRows.Add(new DailyResponseStatsRow
            {
                DateLocal = b.DateLocal,
                ShortLabel = isToday ? $"{b.DateLocal:dd.MM} · сег." : b.DateLocal.ToString("dd.MM", culture),
                WeekdayLabel = b.DateLocal.ToString("ddd", culture),
                Total = b.Total,
                Sent = b.Sent,
                InProgress = b.InProgress,
                ActionRequired = b.ActionRequired,
                Duplicates = b.Duplicates,
                Errors = b.Errors,
                ErrorHeight = maxTotal > 0 ? StackedChartMaxHeight * b.Errors / maxTotal : 0d,
                DuplicateHeight = maxTotal > 0 ? StackedChartMaxHeight * b.Duplicates / maxTotal : 0d,
                ActionRequiredHeight = maxTotal > 0 ? StackedChartMaxHeight * b.ActionRequired / maxTotal : 0d,
                SentHeight = maxTotal > 0 ? StackedChartMaxHeight * b.Sent / maxTotal : 0d,
                InProgressHeight = maxTotal > 0 ? StackedChartMaxHeight * b.InProgress / maxTotal : 0d,
                Tooltip = BuildDayTooltip(b, culture, isToday)
            });
        }

        PeriodTotal = buckets.Sum(x => x.Total);
        PeriodSent = buckets.Sum(x => x.Sent);
        PeriodInProgress = buckets.Sum(x => x.InProgress);
        PeriodActionRequired = buckets.Sum(x => x.ActionRequired);
        PeriodDuplicates = buckets.Sum(x => x.Duplicates);
        PeriodErrors = buckets.Sum(x => x.Errors);
        HasData = PeriodTotal > 0;

        if (buckets.Count > 0)
        {
            var first = buckets[0].DateLocal;
            var last = buckets[^1].DateLocal;
            PeriodRangeText = first.Date == last.Date
                ? first.ToString("d MMMM yyyy", culture)
                : $"{first:dd.MM.yyyy} — {last:dd.MM.yyyy} ({buckets.Count} дн.)";
        }
        else
        {
            PeriodRangeText = string.Empty;
        }

        var w = Math.Max(480d, buckets.Count * 44d);
        TrendCanvasWidth = w;
        var pts = new PointCollection();
        if (buckets.Count == 1)
        {
            var x = w / 2d;
            var y = maxTotal > 0
                ? TrendChartHeight - TrendChartHeight * buckets[0].Total / maxTotal
                : TrendChartHeight;
            pts.Add(new Point(x, y));
        }
        else
        {
            for (var i = 0; i < buckets.Count; i++)
            {
                var x = i * (w / (buckets.Count - 1));
                var t = buckets[i].Total;
                var y = maxTotal > 0 ? TrendChartHeight - TrendChartHeight * t / maxTotal : TrendChartHeight;
                pts.Add(new Point(x, y));
            }
        }

        TotalTrendPoints = pts;
    }

    private static int ParseQuickDays(object? parameter) => parameter switch
    {
        int i => i,
        string s when int.TryParse(s, out var n) => n,
        _ => 14
    };

    /// <summary>Приводит даты к допустимому диапазону и записывает обратно в свойства (даты в полях совпадают с запросом).</summary>
    private (DateTime Start, DateTime End) NormalizeAndSyncPeriodDates()
    {
        var today = DateTime.Today;
        var end = PeriodEndDate?.Date ?? today;
        var start = PeriodStartDate?.Date ?? end.AddDays(-13);
        if (end > today)
        {
            end = today;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        const int maxCalendarDays = 366;
        if ((end - start).Days + 1 > maxCalendarDays)
        {
            start = end.AddDays(-(maxCalendarDays - 1));
        }

        PeriodStartDate = start;
        PeriodEndDate = end;
        return (start, end);
    }

    private static string BuildDayTooltip(DailyResponseBucket b, CultureInfo culture, bool isToday)
    {
        var title = b.DateLocal.ToString("dddd, d MMMM yyyy", culture);
        if (isToday)
        {
            title += " (сегодня)";
        }

        return
            $"{title}\n"
            + $"Всего: {b.Total}\n"
            + $"В CRM: {b.Sent}\n"
            + $"В обработке: {b.InProgress}\n"
            + $"Нужны действия: {b.ActionRequired}\n"
            + $"Дубликаты: {b.Duplicates}\n"
            + $"Ошибки: {b.Errors}";
    }
}

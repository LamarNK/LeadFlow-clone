using System.Collections.ObjectModel;

namespace LeadFlow.Models;

public sealed class DashboardStats
{
    public int NewResponses { get; set; }
    public int TotalToday { get; set; }
    public int SentToCrm { get; set; }
    public int InProgress { get; set; }
    public int Duplicates { get; set; }
    public int Errors { get; set; }
    public int ConnectedAccounts { get; set; }
    public int RequiresAuthorization { get; set; }
    public int ActiveAdsCount { get; set; }
    public int BlockedAdsCount { get; set; }
    public int DraftsCount { get; set; }
    /// <summary>24 точки — отклики по часу суток (локальное время ПК), для построения шкалы с любым шагом.</summary>
    public ObservableCollection<ActivityPoint> HourlyActivity { get; set; } = new();

    /// <summary>Скользящая неделя: сегодня и шесть предыдущих календарных дней (локально).</summary>
    public ObservableCollection<ActivityPoint> WeeklyByDayActivity { get; set; } = new();

    /// <summary>Момент завершения агрегации на сервере (UTC). Отклики с ProcessedAt позже могли не попасть в снимок.</summary>
    public DateTime AggregatedUpToUtc { get; set; }
}

public sealed class ActivityPoint
{
    public string Label { get; set; } = string.Empty;
    /// <summary>Число откликов за интервал слота (час или объединённые часы).</summary>
    public int NewCount { get; set; }
    public int SentCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ErrorCount { get; set; }
    /// <summary>Высота столбца в пикселях (0–56), пересчитывается относительно максимума за день.</summary>
    public double ChartBarHeight { get; set; }
    /// <summary>Календарный день для недельного графика (локально); для почасового графика не задан.</summary>
    public DateTime? LocalDate { get; set; }

    /// <summary>Начало интервала по часу суток (локальное время ПК), 0…23.</summary>
    public int SlotStartHour { get; set; }
    /// <summary>Длина интервала в часах (1, 2, 3, 6…), для подписи и подсказки.</summary>
    public int SlotSpanHours { get; set; } = 1;
    /// <summary>Подсказка при наведении на столбец динамики.</summary>
    public string ChartTooltip { get; set; } = string.Empty;
}

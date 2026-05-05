namespace LeadFlow;

/// <summary>
/// Даты в SQLite через EF часто приходят с <see cref="DateTimeKind.Unspecified"/>; в приложении их пишем как UTC (<see cref="DateTime.UtcNow"/>).
/// </summary>
public static class DateTimeAssumedUtc
{
    /// <summary>
    /// Полуночь локального календарного дня и следующая локальная полночь, переведённые в UTC.
    /// Интервал [utcStart, utcEnd) — это «эти сутки» на часах ПК; в день перехода DST длина не обязана быть 24 UTC-часам
    /// (в отличие от <c>utcStart.AddDays(1)</c>).
    /// Для зон впереди UTC у <paramref name="utcStart"/> может быть предыдущая дата по календарю UTC — так и должно быть.
    /// </summary>
    public static (DateTime UtcStartInclusive, DateTime UtcEndExclusive) GetUtcRangeForLocalCalendarDay(DateTime localCalendarDate)
    {
        var zone = TimeZoneInfo.Local;
        var localStart = new DateTime(localCalendarDate.Year, localCalendarDate.Month, localCalendarDate.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var localEnd = localStart.AddDays(1);
        var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, zone);
        var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localEnd, zone);
        return (utcStart, utcEnd);
    }

    /// <summary>Текущие локальные календарные «сегодня» на ПК, как интервал UTC для фильтрации UTC-меток.</summary>
    public static (DateTime UtcStartInclusive, DateTime UtcEndExclusive) GetUtcRangeForLocalToday() =>
        GetUtcRangeForLocalCalendarDay(DateTime.Today);

    /// <summary>Переводит в локальное время ПК: UTC и «не указан» трактуются как хранение в UTC.</summary>
    public static DateTime ToLocalTimeFromStoredUtc(this DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return utc.ToLocalTime();
    }
}

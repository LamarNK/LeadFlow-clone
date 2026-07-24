namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Когда воркер должен принудительно закрыть все браузеры AdsPower
/// (осиротевшие окна после ручного просмотра оператором, сбои browser/stop).
/// </summary>
public static class AdsPowerBrowserHousekeeping
{
    /// <param name="completedCyclesSinceLastSweep">
    /// Сколько полных циклов мониторинга (с browser/start…browser/stop по аккаунтам)
    /// завершилось с момента последней уборки.
    /// </param>
    /// <param name="lastSweepLocalDate">
    /// Локальная дата последней уборки; <c>null</c>, если ещё не было
    /// (тогда срабатывает только порог по числу циклов, не «конец дня»).
    /// </param>
    /// <param name="todayLocal">Текущая локальная дата (календарный день на ПК воркера).</param>
    /// <param name="everyNCycles">Интервал по числу проходов (по умолчанию из <see cref="MonitoringTiming"/>).</param>
    public static bool ShouldSweep(
        int completedCyclesSinceLastSweep,
        DateOnly? lastSweepLocalDate,
        DateOnly todayLocal,
        int everyNCycles = MonitoringTiming.BrowserHousekeepingEveryNCycles)
    {
        if (everyNCycles < 1)
        {
            everyNCycles = 1;
        }

        // Смена календарного дня после уже бывшей уборки — «конец дня» / утро с «хвостами» окон.
        if (lastSweepLocalDate is not null && todayLocal != lastSweepLocalDate.Value)
        {
            return true;
        }

        return completedCyclesSinceLastSweep >= everyNCycles;
    }
}

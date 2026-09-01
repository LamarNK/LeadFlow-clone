namespace LeadFlow.Core.Services;

/// <summary>Пауза между полными циклами мониторинга по результатам только что завершённого цикла.</summary>
public static class MonitoringCycleDelay
{
    /// <param name="consecutiveQuietCycles">Сколько полных циклов подряд закончилось без новых откликов (для текущего уже учтён).</param>
    /// <param name="hasUndischargedBacklog">У хотя бы одного аккаунта новых откликов больше, чем успеваем обработать за цикл.</param>
    /// <param name="historicalHeatScore">0…1 — насколько текущий локальный слот исторически «горячий» (см. <see cref="MonitoringHistoricalHeat"/>).</param>
    public static TimeSpan GetDelayAfterCycle(
        int newResponsesInCycle,
        int accountsPolled,
        int consecutiveQuietCycles = 0,
        bool hasUndischargedBacklog = false,
        double historicalHeatScore = 0)
    {
        var min = MonitoringTiming.CycleDelayMinMinutes;
        var max = MonitoringTiming.CycleDelayMaxMinutes;
        if (min >= max)
        {
            return TimeSpan.FromMinutes(min);
        }

        var capacity = Math.Max(1, accountsPolled * MonitoringTiming.TypicalResponsesPerAccountPerCycle);
        var activity = Math.Clamp((double)newResponsesInCycle / capacity, 0, 1);

        // Единичный отклик не считается высокой нагрузкой: ёмкость цикла —
        // TypicalResponsesPerAccountPerCycle на аккаунт (обычно 30). Один новый
        // отклик на одном аккаунте даёт activity ≈ 1/30, пауза остаётся близкой
        // к максимуму. Нормализация newResponses/accountsPolled раньше поднимала
        // любой ненулевой отклик до activity=1 и ставила следующий проход через
        // min (3 мин) — слишком часто для Avito при редких единичных откликах.

        if (hasUndischargedBacklog)
        {
            activity = 1;
        }

        if (historicalHeatScore > 0)
        {
            var heatBoost = Math.Clamp(historicalHeatScore, 0, 1) * MonitoringTiming.CycleHistoricalHeatActivityBoostCap;
            activity = Math.Max(activity, heatBoost);
        }

        var minutes = max - activity * (max - min);

        if (newResponsesInCycle == 0 && consecutiveQuietCycles > 1)
        {
            var extra = Math.Min(
                MonitoringTiming.CycleQuietBackoffExtraMinutesMax,
                (consecutiveQuietCycles - 1) * MonitoringTiming.CycleQuietBackoffExtraMinutesPerStep);
            minutes += extra;
        }

        var absoluteMax = max + MonitoringTiming.CycleQuietBackoffExtraMinutesMax;
        if (newResponsesInCycle == 0 && consecutiveQuietCycles > 1 && minutes >= absoluteMax)
        {
            // Вместо жесткого потолка (например, ровно 60 мин) добавляем небольшой джиттер.
            var randomizedTop = absoluteMax - 15 + Random.Shared.NextDouble() * 15;
            minutes = randomizedTop;
        }

        minutes = Math.Clamp(minutes, min, absoluteMax);
        return TimeSpan.FromMinutes(minutes);
    }
}

namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Пауза до следующего прохода аккаунта. Явный <c>RetryAfter</c> (CDP hang,
/// Local API queue/HTTP timeout) не поднимается ночным полом 90–180 мин —
/// слот должен вернуться примерно через минуту.
/// Успешный дневной проход без backlog получает ±<see cref="MonitoringTiming.CycleDelayJitterPercent"/>%
/// к паузе, чтобы аккаунты не ходили в Avito синхронно; ночной floor уже случайный сам по себе.
/// </summary>
internal static class WorkerAccountPassDelay
{
    public static TimeSpan Resolve(
        TimeSpan? retryAfter,
        bool polled,
        int newResponses,
        int quietStreak,
        bool backlog,
        double historicalHeat,
        DateTime utcNow)
    {
        if (retryAfter is { } requested)
        {
            return requested;
        }

        var delay = polled
            ? MonitoringCycleDelay.GetDelayAfterCycle(
                newResponses,
                accountsPolled: 1,
                quietStreak,
                backlog,
                historicalHeat)
            : TimeSpan.FromMinutes(MonitoringTiming.CycleDelayMinMinutes);

        if (!backlog)
        {
            delay = ApplyDaytimeJitter(delay);
        }

        return MonitoringNightQuiet.ApplyFloor(delay, utcNow);
    }

    /// <summary>
    /// ±<see cref="MonitoringTiming.CycleDelayJitterPercent"/>% к дневной паузе.
    /// Не опускается ниже min и не поднимается выше max, если расчёт уже в [min..max];
    /// quiet-backoff сверх max не урезается.
    /// </summary>
    private static TimeSpan ApplyDaytimeJitter(TimeSpan delay)
    {
        var min = (double)MonitoringTiming.CycleDelayMinMinutes;
        var max = (double)MonitoringTiming.CycleDelayMaxMinutes;
        var amplitude = MonitoringTiming.CycleDelayJitterPercent / 100.0;
        var factor = 1.0 + ((Random.Shared.NextDouble() * 2.0) - 1.0) * amplitude;
        var minutes = delay.TotalMinutes * factor;
        var ceiling = Math.Max(max, delay.TotalMinutes);
        return TimeSpan.FromMinutes(Math.Clamp(minutes, min, ceiling));
    }
}

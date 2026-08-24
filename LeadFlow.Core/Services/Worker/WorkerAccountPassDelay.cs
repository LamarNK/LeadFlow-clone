namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Пауза до следующего прохода аккаунта. Явный <c>RetryAfter</c> (CDP hang,
/// Local API queue/HTTP timeout) не поднимается ночным полом 45–90 мин —
/// слот должен вернуться примерно через минуту.
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

        return MonitoringNightQuiet.ApplyFloor(delay, utcNow);
    }
}

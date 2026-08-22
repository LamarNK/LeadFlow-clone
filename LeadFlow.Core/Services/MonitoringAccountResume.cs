namespace LeadFlow.Core.Services;

/// <summary>
/// Когда аккаунт снова можно брать после рестарта процесса.
/// In-memory пауза пропадает вместе с воркером — смотрим сохранённый next, иначе LastMonitoringAt + мин. пауза.
/// </summary>
public static class MonitoringAccountResume
{
    public static DateTime ResolveNextEligibleUtc(
        DateTime? memoryNextUtc,
        DateTime? persistedNextUtc,
        DateTime? lastMonitoringAtUtc)
    {
        if (memoryNextUtc is { } memory)
        {
            return memory;
        }

        if (persistedNextUtc is { } persisted)
        {
            return persisted;
        }

        if (lastMonitoringAtUtc is { } last)
        {
            return last.AddMinutes(MonitoringTiming.CycleDelayMinMinutes);
        }

        return DateTime.MinValue;
    }
}

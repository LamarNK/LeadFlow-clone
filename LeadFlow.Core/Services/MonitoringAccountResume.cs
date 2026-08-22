namespace LeadFlow.Core.Services;

/// <summary>
/// Пауза аккаунта и «уже прошли эти субпрофили в текущем проходе» — переживают рестарт воркера.
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

    public static bool IsUnfinishedPass(DateTime? passStartedAtUtc, DateTime? passFinishedAtUtc) =>
        passStartedAtUtc is not null
        && (passFinishedAtUtc is null || passStartedAtUtc > passFinishedAtUtc);

    public static void BeginOrResumePass(
        DateTime utcNow,
        ref DateTime? passStartedAtUtc,
        ref DateTime? passFinishedAtUtc,
        HashSet<string> completedSubIds)
    {
        ArgumentNullException.ThrowIfNull(completedSubIds);
        if (IsUnfinishedPass(passStartedAtUtc, passFinishedAtUtc))
        {
            return;
        }

        passStartedAtUtc = utcNow;
        passFinishedAtUtc = null;
        completedSubIds.Clear();
    }

    public static void MarkSubCompleted(HashSet<string> completedSubIds, string? subProfileId)
    {
        ArgumentNullException.ThrowIfNull(completedSubIds);
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return;
        }

        completedSubIds.Add(subProfileId.Trim());
    }

    public static void FinishPass(
        DateTime utcNow,
        DateTime nextEligibleUtc,
        ref DateTime? passStartedAtUtc,
        ref DateTime? passFinishedAtUtc,
        ref DateTime? nextMonitoringAtUtc,
        HashSet<string> completedSubIds)
    {
        ArgumentNullException.ThrowIfNull(completedSubIds);
        passStartedAtUtc ??= utcNow;
        passFinishedAtUtc = utcNow;
        nextMonitoringAtUtc = nextEligibleUtc;
        completedSubIds.Clear();
    }

    public static List<T> RemainingSubProfiles<T>(
        IEnumerable<T> subProfiles,
        Func<T, string> idSelector,
        DateTime? passStartedAtUtc,
        DateTime? passFinishedAtUtc,
        IReadOnlySet<string> completedSubIds)
    {
        ArgumentNullException.ThrowIfNull(subProfiles);
        ArgumentNullException.ThrowIfNull(idSelector);
        ArgumentNullException.ThrowIfNull(completedSubIds);

        var list = subProfiles as IList<T> ?? subProfiles.ToList();
        if (!IsUnfinishedPass(passStartedAtUtc, passFinishedAtUtc) || completedSubIds.Count == 0)
        {
            return list.ToList();
        }

        return list
            .Where(item =>
            {
                var id = idSelector(item);
                return string.IsNullOrWhiteSpace(id) || !completedSubIds.Contains(id.Trim());
            })
            .ToList();
    }
}

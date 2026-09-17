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

    /// <summary>
    /// Начинает новый проход или продолжает незавершённый.
    /// Возвращает true, если начат НОВЫЙ проход: вызывающий код обязан сбросить
    /// связанные с проходом счётчики (бюджет действий на аккаунте), потому что
    /// <see cref="IsUnfinishedPass"/> больше не отличает их от счётчиков активного прохода.
    /// false — проход возобновлён, состояние прохода (включая счётчики) сохраняется.
    /// </summary>
    public static bool BeginOrResumePass(
        DateTime utcNow,
        ref DateTime? passStartedAtUtc,
        ref DateTime? passFinishedAtUtc,
        HashSet<string> completedSubIds)
    {
        ArgumentNullException.ThrowIfNull(completedSubIds);
        if (IsUnfinishedPass(passStartedAtUtc, passFinishedAtUtc))
        {
            return false;
        }

        passStartedAtUtc = utcNow;
        passFinishedAtUtc = null;
        completedSubIds.Clear();
        return true;
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

namespace Orbita.Contracts;

/// <summary>
/// Pure business rules for distributing Bitrix24 deals between managers.
/// The policy has no HTTP, database or Bitrix24 dependencies so it can also
/// be reused when the workflow is moved from Bitrix24 to the native CRM.
/// </summary>
public static class BitrixWorkforceDistribution
{
    public const string DisabledMode = "disabled";
    public const string ShadowMode = "shadow";
    public const string WriterMode = "writer";

    public const string NewScenario = "new";
    public const string MissedCallScenario = "missed_call";
    public const string SubstituteMissedCallScenario = "substitute_missed_call";

    public const string TimemanOpened = "OPENED";
    public const string TimemanPaused = "PAUSED";

    public sealed record StageMap(
        string New,
        string MissedCall,
        string MissedCallSecondary,
        string SubstituteMissedCall,
        string SubstituteMissedCallSecondary);

    public sealed record Scenario(
        string Name,
        string SourceStageId,
        string TargetStageId,
        bool UsesMorningWindow);

    public sealed record ManagerCandidate(
        long BitrixUserId,
        string? TimemanStatus,
        bool IsActive = true);

    public static Scenario? Classify(string? stageId, StageMap stages)
    {
        if (string.IsNullOrWhiteSpace(stageId))
        {
            return null;
        }

        if (StageEquals(stageId, stages.New))
        {
            return new Scenario(NewScenario, stageId, stages.New, UsesMorningWindow: false);
        }

        if (StageEquals(stageId, stages.MissedCall))
        {
            return new Scenario(MissedCallScenario, stageId, stages.MissedCall, UsesMorningWindow: true);
        }

        if (StageEquals(stageId, stages.MissedCallSecondary))
        {
            return new Scenario(MissedCallScenario, stageId, stages.MissedCall, UsesMorningWindow: true);
        }

        if (StageEquals(stageId, stages.SubstituteMissedCall))
        {
            return new Scenario(
                SubstituteMissedCallScenario,
                stageId,
                stages.SubstituteMissedCall,
                UsesMorningWindow: true);
        }

        if (StageEquals(stageId, stages.SubstituteMissedCallSecondary))
        {
            return new Scenario(
                SubstituteMissedCallScenario,
                stageId,
                stages.SubstituteMissedCall,
                UsesMorningWindow: true);
        }

        return null;
    }

    public static IReadOnlyList<long> EligibleManagerIds(IEnumerable<ManagerCandidate> managers)
    {
        ArgumentNullException.ThrowIfNull(managers);

        var result = new List<long>();
        var seen = new HashSet<long>();
        foreach (var manager in managers)
        {
            if (manager.BitrixUserId <= 0
                || !manager.IsActive
                || !IsWorkingStatus(manager.TimemanStatus)
                || !seen.Add(manager.BitrixUserId))
            {
                continue;
            }

            result.Add(manager.BitrixUserId);
        }

        return result;
    }

    /// <summary>
    /// Selects the next manager in the configured order. If the previously
    /// selected manager is no longer eligible, the first eligible manager wins.
    /// </summary>
    public static long? SelectNextManager(
        IEnumerable<long> eligibleManagerIds,
        long? lastManagerId)
    {
        ArgumentNullException.ThrowIfNull(eligibleManagerIds);

        var eligible = eligibleManagerIds
            .Where(x => x > 0)
            .Distinct()
            .ToList();
        if (eligible.Count == 0)
        {
            return null;
        }

        if (lastManagerId is null)
        {
            return eligible[0];
        }

        var lastIndex = eligible.IndexOf(lastManagerId.Value);
        return lastIndex < 0 || lastIndex == eligible.Count - 1
            ? eligible[0]
            : eligible[lastIndex + 1];
    }

    public static bool IsInsideWindow(
        DateTimeOffset instant,
        TimeZoneInfo timeZone,
        TimeOnly start,
        TimeOnly end)
    {
        ValidateWindow(start, end);
        var localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);
        return localTime >= start && localTime < end;
    }

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

    public static DateTimeOffset NextWindowStart(
        DateTimeOffset instant,
        TimeZoneInfo timeZone,
        TimeOnly start,
        TimeOnly end)
    {
        ValidateWindow(start, end);

        var localNow = TimeZoneInfo.ConvertTime(instant, timeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var localTime = TimeOnly.FromDateTime(localNow.DateTime);
        if (localTime >= end)
        {
            localDate = localDate.AddDays(1);
        }

        var localStart = localDate.ToDateTime(start, DateTimeKind.Unspecified);
        var offset = timeZone.GetUtcOffset(localStart);
        return new DateTimeOffset(localStart, offset);
    }

    /// <summary>
    /// Calculates how many queued deals may be released to the first manager.
    /// The percentage is explicit configuration because the exact legacy
    /// Andrey2 value is not available.
    /// </summary>
    public static int CalculateInitialReleaseCount(int queuedDeals, decimal releasePercent)
    {
        if (queuedDeals <= 0 || releasePercent <= 0)
        {
            return 0;
        }

        var normalizedPercent = Math.Min(100m, releasePercent);
        return Math.Min(
            queuedDeals,
            (int)Math.Ceiling(queuedDeals * normalizedPercent / 100m));
    }

    public static bool CanReleaseReservedDeals(
        int eligibleManagerCount,
        DateTimeOffset instant,
        DateTimeOffset reserveUntil) =>
        eligibleManagerCount >= 2 || instant >= reserveUntil;

    private static bool IsWorkingStatus(string? status) =>
        string.Equals(status, TimemanOpened, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, TimemanPaused, StringComparison.OrdinalIgnoreCase);

    private static bool StageEquals(string actual, string expected) =>
        !string.IsNullOrWhiteSpace(expected)
        && string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void ValidateWindow(TimeOnly start, TimeOnly end)
    {
        if (start >= end)
        {
            throw new ArgumentException("The distribution window must start before it ends.");
        }
    }
}

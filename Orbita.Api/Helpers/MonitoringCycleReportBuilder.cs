using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orbita.Contracts;

namespace Orbita.Api.Helpers;

internal enum MonitoringCycleLogEventType
{
    Switch,
    Completion,
    Stats,
    Error
}

internal sealed record MonitoringCycleLogEvent(
    string AccountName,
    DateTime TimestampUtc,
    MonitoringCycleLogEventType Type,
    string SubProfileName,
    int SubProfileIndex,
    int SubProfileTotal,
    int? Leads,
    string? ErrorDetail);

internal sealed record MonitoringCycleSentResponse(
    string AccountName,
    string SubProfileName,
    DateTime TimestampUtc);

/// <summary>Снимок цикла из типизированного журнала (без логов).</summary>
internal sealed record MonitoringCycleRunSnapshot(
    Guid Id,
    string AccountName,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    string Status,
    IReadOnlyList<MonitoringSubProfileRunSnapshot> SubProfiles);

internal sealed record MonitoringSubProfileRunSnapshot(
    Guid Id,
    string SubProfileId,
    string SubProfileName,
    int Position,
    int Total,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    string Outcome,
    string? ErrorType,
    string? ErrorMessage,
    int PublishedCount);

internal static partial class MonitoringCycleReportBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"переключаем суб-профиль\s+(\d+)/(\d+).*?для\s+([^:]+):\s*(.+?)\s*\(id=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SwitchFromMessageRegex();

    [GeneratedRegex(@"Суб-профиль «([^»]+)» аккаунта\s+([^:]+):\s*новых для LeadFlow\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex CompletionFromMessageRegex();

    [GeneratedRegex(@"Суб-профиль «([^»]+)» \(объявления\):", RegexOptions.CultureInvariant)]
    private static partial Regex StatsFromMessageRegex();

    [GeneratedRegex(@"Не удалось обработать суб-профиль «([^»]+)».*аккаунта\s+([^:]+):\s*(.+)", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorFromMessageRegex();

    [GeneratedRegex(@"переключаем суб-профиль", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SwitchMarkerRegex();

    [GeneratedRegex(@"обработано сейчас\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessedNowFromMessageRegex();

    [GeneratedRegex(@"Аккаунт «([^»]+)»[^·]*· «([^»]+)» — опубликовано\s*(\d+)\s*из", RegexOptions.CultureInvariant)]
    private static partial Regex PublishedFromMessageRegex();

    [GeneratedRegex(@"(\d+)/(\d+)")]
    private static partial Regex PositionFromMessageRegex();

    [GeneratedRegex(@"Аккаунт «([^»]+)»[^·]*· «([^»]+)» \((\d+)/(\d+)\): переключение субпрофиля", RegexOptions.CultureInvariant)]
    private static partial Regex NewWorkerSwitchRegex();

    [GeneratedRegex(@"Аккаунт «([^»]+)»[^·]*· «([^»]+)» — отклики:", RegexOptions.CultureInvariant)]
    private static partial Regex NewWorkerExtractionMarkerRegex();

    [GeneratedRegex(@"Аккаунт «([^»]+)»[^·]*· «([^»]+)» — проблема \([^)]+\): (.+)", RegexOptions.CultureInvariant)]
    private static partial Regex NewWorkerSubProfileIssueRegex();

    [GeneratedRegex(@"Аккаунт «([^»]+)»[^·]*· «([^»]+)» — не переключился: (.+)", RegexOptions.CultureInvariant)]
    private static partial Regex NewWorkerSwitchFailedRegex();

    [GeneratedRegex(@"переключение субпрофиля", RegexOptions.CultureInvariant)]
    private static partial Regex NewWorkerSwitchMarkerRegex();

    /// <summary>
    /// Полный отчёт из типизированного журнала запусков (+ лиды из Sent-откликов).
    /// </summary>
    public static MonitoringCycleReportDto BuildFromJournal(
        IReadOnlyList<MonitoringCycleRunSnapshot> cycles,
        DateTime startLocal,
        DateTime endLocal,
        IReadOnlySet<string>? allowedAccountNames = null,
        IReadOnlyList<MonitoringCycleSentResponse>? sentResponses = null)
    {
        // Journal has full cycle structure for any period length (day / week / month).
        // IsDetailed no longer depends on single-day — that limit was only for log-scan cost.
        const bool isDetailed = true;
        var sent = sentResponses ?? [];
        var filteredCycles = cycles
            .Where(c => allowedAccountNames is null || allowedAccountNames.Contains(c.AccountName))
            .Where(c =>
            {
                var day = LocalCalendarDateRange.ToLocalDateFromStoredUtc(c.StartedAtUtc);
                return day >= startLocal.Date && day <= endLocal.Date;
            })
            .OrderBy(c => c.StartedAtUtc)
            .ToList();

        if (filteredCycles.Count == 0)
        {
            // No journal rows yet — still show Bitrix lead totals for the period.
            return BuildSummaryFromSentResponses(sent, startLocal, endLocal, allowedAccountNames);
        }

        var reports = new List<MonitoringCycleAccountReportDto>();
        var notStartedSummaries = new List<string>();
        var leadSummaries = new List<MonitoringCycleLeadSummaryDto>();
        var totalNotStartedPositions = 0;
        var accountsWithNotStarted = 0;

        foreach (var day in EnumerateDays(startLocal, endLocal))
        {
            var dayCycles = filteredCycles
                .Where(c => LocalCalendarDateRange.ToLocalDateFromStoredUtc(c.StartedAtUtc) == day.Date)
                .ToList();
            if (dayCycles.Count == 0)
            {
                continue;
            }

            var byAccount = dayCycles
                .GroupBy(c => c.AccountName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => ExtractAccountSortKey(g.Key));

            foreach (var accountGroup in byAccount)
            {
                var accountName = accountGroup.Key;
                var accountCycles = accountGroup.OrderBy(c => c.StartedAtUtc).ToList();
                var posToName = new Dictionary<int, string>();
                var posTimes = new Dictionary<int, List<DateTime?>>();
                var posLeads = new Dictionary<int, List<string?>>();
                var posErrors = new Dictionary<int, List<MonitoringCycleErrorDto>>();
                var totalPositions = 0;

                foreach (var cycle in accountCycles)
                {
                    foreach (var sp in cycle.SubProfiles.OrderBy(x => x.Position))
                    {
                        totalPositions = Math.Max(totalPositions, Math.Max(sp.Total, sp.Position));
                        if (!posToName.ContainsKey(sp.Position))
                        {
                            posToName[sp.Position] = sp.SubProfileName;
                        }

                        if (!posTimes.ContainsKey(sp.Position))
                        {
                            posTimes[sp.Position] = [];
                            posLeads[sp.Position] = [];
                            posErrors[sp.Position] = [];
                        }
                    }
                }

                if (totalPositions <= 0)
                {
                    totalPositions = posToName.Keys.DefaultIfEmpty(1).Max();
                }

                // Ensure all positions 1..total known from any cycle are tracked.
                for (var position = 1; position <= totalPositions; position++)
                {
                    if (!posToName.ContainsKey(position))
                    {
                        // Unknown name until observed; keep placeholder only if some cycle declared total.
                        continue;
                    }

                    posTimes.TryAdd(position, []);
                    posLeads.TryAdd(position, []);
                    posErrors.TryAdd(position, []);
                }

                for (var cycleIndex = 0; cycleIndex < accountCycles.Count; cycleIndex++)
                {
                    var cycle = accountCycles[cycleIndex];
                    var nextCycleStart = cycleIndex + 1 < accountCycles.Count
                        ? accountCycles[cycleIndex + 1].StartedAtUtc
                        : LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(day).UtcEndExclusive;
                    var cycleEnd = cycle.FinishedAtUtc ?? nextCycleStart;
                    var runsByPosition = cycle.SubProfiles
                        .GroupBy(x => x.Position)
                        .ToDictionary(g => g.Key, g => g.OrderBy(x => x.StartedAtUtc).Last());

                    var declaredTotal = cycle.SubProfiles.Select(x => x.Total).DefaultIfEmpty(totalPositions).Max();
                    declaredTotal = Math.Max(declaredTotal, totalPositions);

                    for (var position = 1; position <= declaredTotal; position++)
                    {
                        if (!posToName.ContainsKey(position) && !runsByPosition.ContainsKey(position))
                        {
                            // Position never declared for this account on this day — skip until we know the name.
                            if (runsByPosition.Count > 0)
                            {
                                // Still track not-started slots when total is known from other positions.
                                var sampleTotal = cycle.SubProfiles.Select(x => x.Total).DefaultIfEmpty(0).Max();
                                if (sampleTotal < position)
                                {
                                    continue;
                                }

                                posToName[position] = $"#{position}";
                                posTimes.TryAdd(position, []);
                                posLeads.TryAdd(position, []);
                                posErrors.TryAdd(position, []);
                            }
                            else
                            {
                                continue;
                            }
                        }

                        if (!posTimes.ContainsKey(position))
                        {
                            posTimes[position] = [];
                            posLeads[position] = [];
                            posErrors[position] = [];
                        }

                        if (!runsByPosition.TryGetValue(position, out var run))
                        {
                            // Not started in this cycle (aborted before queue).
                            posTimes[position].Add(null);
                            posLeads[position].Add(null);
                            if (cycleIndex == accountCycles.Count - 1
                                && cycle.Status is MonitoringCycleRunStatuses.Aborted
                                    or MonitoringCycleRunStatuses.Failed
                                    or MonitoringCycleRunStatuses.Running)
                            {
                                posErrors[position].Add(new MonitoringCycleErrorDto(
                                    cycle.FinishedAtUtc ?? cycle.StartedAtUtc,
                                    "Не запущен (прервано до очереди)"));
                            }

                            continue;
                        }

                        posToName[position] = run.SubProfileName;
                        if (run.Outcome == MonitoringSubProfileRunOutcomes.Completed
                            && run.CompletedAtUtc is DateTime completedAt)
                        {
                            posTimes[position].Add(completedAt);
                            var windowEnd = cycle.SubProfiles
                                .Where(x => x.Position > position)
                                .OrderBy(x => x.Position)
                                .Select(x => (DateTime?)x.StartedAtUtc)
                                .FirstOrDefault() ?? cycleEnd;
                            var sentInWindow = CountSentInWindow(
                                sent,
                                accountName,
                                run.SubProfileName,
                                run.StartedAtUtc,
                                windowEnd);
                            // Prefer delivery-based count; fall back to journal published counter.
                            var leadCount = sentInWindow > 0 ? sentInWindow : run.PublishedCount;
                            posLeads[position].Add(leadCount > 0
                                ? leadCount.ToString(CultureInfo.InvariantCulture)
                                : "—");
                        }
                        else if (run.Outcome == MonitoringSubProfileRunOutcomes.Failed)
                        {
                            posTimes[position].Add(null);
                            posLeads[position].Add(null);
                            var detail = !string.IsNullOrWhiteSpace(run.ErrorMessage)
                                ? run.ErrorMessage!
                                : !string.IsNullOrWhiteSpace(run.ErrorType)
                                    ? run.ErrorType!
                                    : "Ошибка прохода";
                            if (detail.Length > 80)
                            {
                                detail = detail[..80];
                            }

                            posErrors[position].Add(new MonitoringCycleErrorDto(
                                run.CompletedAtUtc ?? run.StartedAtUtc,
                                detail));
                        }
                        else
                        {
                            // Started / skipped without completion.
                            posTimes[position].Add(null);
                            posLeads[position].Add(null);
                        }
                    }

                    totalPositions = Math.Max(totalPositions, declaredTotal);
                }

                var notStarted = posToName.Keys
                    .Where(position => posTimes.TryGetValue(position, out var times) && times.All(t => t is null))
                    .OrderBy(x => x)
                    .ToList();

                if (notStarted.Count > 0)
                {
                    accountsWithNotStarted++;
                    totalNotStartedPositions += notStarted.Count;
                    var names = string.Join(", ", notStarted.Select(p => $"{p}/{totalPositions} ({posToName[p]})"));
                    notStartedSummaries.Add($"  {accountName}: {notStarted.Count} не запущены — {names}");
                }

                var leadTotal = CountSentForAccountOnDay(sent, accountName, day);
                var leadParts = new List<string>();
                foreach (var position in posToName.Keys.OrderBy(x => x))
                {
                    var positionLeadTotal = CountSentForSubProfileOnDay(
                        sent,
                        accountName,
                        posToName[position],
                        day);
                    if (positionLeadTotal > 0)
                    {
                        leadParts.Add($"{position}/{totalPositions} ({posToName[position]}) = {positionLeadTotal}");
                    }
                }

                leadSummaries.Add(new MonitoringCycleLeadSummaryDto(
                    accountName,
                    leadTotal,
                    leadParts));

                var rows = posToName.Keys
                    .OrderBy(x => x)
                    .Select(position =>
                    {
                        var times = posTimes[position].Where(t => t is not null).Select(t => t!.Value).ToList();
                        var leads = posLeads[position].Where(v => v is not null).Select(v => v!).ToList();
                        var errors = posErrors[position]
                            .GroupBy(e => (e.TimestampUtc, e.Detail))
                            .Select(g => g.First())
                            .OrderBy(e => e.TimestampUtc)
                            .ToList();
                        return new MonitoringCycleSubProfileRowDto(
                            position,
                            totalPositions,
                            posToName[position],
                            times,
                            leads,
                            errors);
                    })
                    .ToList();

                var dateUtc = LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(day).UtcStartInclusive;
                reports.Add(new MonitoringCycleAccountReportDto(
                    accountName,
                    dateUtc,
                    posToName.Count,
                    accountCycles.Count,
                    leadTotal,
                    rows,
                    notStarted.Select(p => $"{p}/{totalPositions} ({posToName[p]})").ToList()));
            }
        }

        var mergedLeadSummaries = leadSummaries
            .GroupBy(x => x.AccountName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MonitoringCycleLeadSummaryDto(
                g.Key,
                g.Sum(x => x.TotalLeads),
                g.SelectMany(x => x.Breakdown).Distinct(StringComparer.Ordinal).ToList()))
            .OrderBy(x => ExtractAccountSortKey(x.AccountName))
            .ToList();

        // Accounts with sent leads but no journal cycles still appear in totals.
        var journalAccountNames = mergedLeadSummaries
            .Select(x => x.AccountName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sentOnly = BuildSummaryFromSentResponses(sent, startLocal, endLocal, allowedAccountNames);
        foreach (var extra in sentOnly.LeadSummaries)
        {
            if (journalAccountNames.Contains(extra.AccountName))
            {
                continue;
            }

            mergedLeadSummaries.Add(extra);
        }

        mergedLeadSummaries = mergedLeadSummaries
            .OrderBy(x => ExtractAccountSortKey(x.AccountName))
            .ToList();

        return new MonitoringCycleReportDto(
            isDetailed,
            mergedLeadSummaries.Sum(x => x.TotalLeads),
            accountsWithNotStarted,
            totalNotStartedPositions,
            notStartedSummaries,
            mergedLeadSummaries,
            reports
                .OrderBy(x => x.DateUtc)
                .ThenBy(x => ExtractAccountSortKey(x.AccountName))
                .ToList());
    }

    /// <summary>
    /// Сводка «Мониторинг циклов» только из отправленных откликов (без логов и без журнала).
    /// </summary>
    public static MonitoringCycleReportDto BuildSummaryFromSentResponses(
        IReadOnlyList<MonitoringCycleSentResponse> sentResponses,
        DateTime startLocal,
        DateTime endLocal,
        IReadOnlySet<string>? allowedAccountNames = null)
    {
        var start = startLocal.Date;
        var end = endLocal.Date;
        var filtered = sentResponses
            .Where(x =>
            {
                if (allowedAccountNames is not null && !allowedAccountNames.Contains(x.AccountName))
                {
                    return false;
                }

                var localDate = LocalCalendarDateRange.ToLocalDateFromStoredUtc(x.TimestampUtc);
                return localDate >= start && localDate <= end;
            })
            .ToList();

        if (filtered.Count == 0)
        {
            return Empty(isDetailed: false);
        }

        var leadSummaries = filtered
            .GroupBy(x => x.AccountName, StringComparer.OrdinalIgnoreCase)
            .Select(accountGroup =>
            {
                var breakdown = accountGroup
                    .GroupBy(
                        x => string.IsNullOrWhiteSpace(x.SubProfileName) ? "—" : x.SubProfileName.Trim(),
                        StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => $"{g.Key} = {g.Count().ToString(CultureInfo.InvariantCulture)}")
                    .ToList();

                return new MonitoringCycleLeadSummaryDto(
                    accountGroup.First().AccountName,
                    accountGroup.Count(),
                    breakdown);
            })
            .OrderBy(x => ExtractAccountSortKey(x.AccountName))
            .ToList();

        return new MonitoringCycleReportDto(
            IsDetailed: false,
            TotalLeads: leadSummaries.Sum(x => x.TotalLeads),
            AccountsWithNotStarted: 0,
            NotStartedPositions: 0,
            NotStartedSummaries: [],
            LeadSummaries: leadSummaries,
            AccountReports: []);
    }

    public static MonitoringCycleReportDto Build(
        IEnumerable<(DateTime TimestampUtc, string Message, string? PropertiesJson)> logRows,
        DateTime startLocal,
        DateTime endLocal,
        IReadOnlySet<string>? allowedAccountNames = null,
        IReadOnlyList<MonitoringCycleSentResponse>? sentResponses = null)
    {
        var isDetailed = (endLocal.Date - startLocal.Date).Days == 0;
        var sent = sentResponses ?? [];

        // Multi-day UI shows only lead totals; rebuild from data and ignore logs.
        if (!isDetailed)
        {
            return BuildSummaryFromSentResponses(sent, startLocal, endLocal, allowedAccountNames);
        }

        var events = ParseEvents(logRows, allowedAccountNames);
        if (events.Count == 0)
        {
            // Even for a single day, fall back to response data when cycle logs are missing.
            if (sent.Count > 0)
            {
                var fallback = BuildSummaryFromSentResponses(sent, startLocal, endLocal, allowedAccountNames);
                return fallback with { IsDetailed = true };
            }

            return Empty(isDetailed);
        }

        var reports = new List<MonitoringCycleAccountReportDto>();
        var notStartedSummaries = new List<string>();
        var leadSummaries = new List<MonitoringCycleLeadSummaryDto>();
        var totalNotStartedPositions = 0;
        var accountsWithNotStarted = 0;

        foreach (var day in EnumerateDays(startLocal, endLocal))
        {
            var dayEvents = events
                .Where(e => LocalCalendarDateRange.ToLocalDateFromStoredUtc(e.TimestampUtc) == day.Date)
                .OrderBy(e => e.TimestampUtc)
                .ToList();

            var byAccount = dayEvents
                .GroupBy(e => e.AccountName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => ExtractAccountSortKey(g.Key));

            foreach (var accountGroup in byAccount)
            {
                var accountName = accountGroup.Key;
                var accountEvents = accountGroup.ToList();
                var switches = accountEvents.Where(e => e.Type == MonitoringCycleLogEventType.Switch).ToList();
                if (switches.Count == 0)
                {
                    continue;
                }

                var posToName = switches
                    .GroupBy(s => s.SubProfileIndex)
                    .ToDictionary(g => g.Key, g => g.First().SubProfileName);

                var cycles = SplitCycles(switches);
                var cycleCount = cycles.Count;
                var posTimes = new Dictionary<int, List<DateTime?>>();
                var posLeads = new Dictionary<int, List<string?>>();
                var posErrors = new Dictionary<int, List<MonitoringCycleErrorDto>>();

                foreach (var position in posToName.Keys)
                {
                    posTimes[position] = [];
                    posLeads[position] = [];
                    posErrors[position] = [];
                }

                var lastCyclePositions = cycles[^1].Select(s => s.SubProfileIndex).ToHashSet();
                var dayUtcEnd = LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(day).UtcEndExclusive;

                for (var cycleIndex = 0; cycleIndex < cycles.Count; cycleIndex++)
                {
                    var cycle = cycles[cycleIndex];
                    var nextCycleStartUtc = cycleIndex + 1 < cycles.Count
                        ? cycles[cycleIndex + 1][0].TimestampUtc
                        : dayUtcEnd;

                    foreach (var sw in cycle)
                    {
                        var position = sw.SubProfileIndex;
                        var name = sw.SubProfileName;
                        var windowStartUtc = sw.TimestampUtc;
                        var nextInCycle = cycle.FirstOrDefault(x => x.SubProfileIndex > position);
                        var windowEndUtc = nextInCycle is not null
                            ? nextInCycle.TimestampUtc
                            : nextCycleStartUtc;

                        var okEntries = accountEvents
                            .Where(e => e.Type == MonitoringCycleLogEventType.Completion
                                && string.Equals(e.SubProfileName, name, StringComparison.OrdinalIgnoreCase)
                                && e.TimestampUtc >= windowStartUtc
                                && e.TimestampUtc < windowEndUtc)
                            .ToList();
                        var statsEntries = accountEvents
                            .Where(e => e.Type == MonitoringCycleLogEventType.Stats
                                && string.Equals(e.SubProfileName, name, StringComparison.OrdinalIgnoreCase)
                                && e.TimestampUtc >= windowStartUtc
                                && e.TimestampUtc < windowEndUtc)
                            .ToList();

                        var completionEvents = okEntries
                            .Concat(statsEntries)
                            .OrderBy(e => e.TimestampUtc)
                            .ToList();

                        var errors = accountEvents
                            .Where(e => e.Type == MonitoringCycleLogEventType.Error
                                && string.Equals(e.SubProfileName, name, StringComparison.OrdinalIgnoreCase)
                                && e.TimestampUtc >= windowStartUtc
                                && e.TimestampUtc < windowEndUtc)
                            .Select(e => new MonitoringCycleErrorDto(e.TimestampUtc, e.ErrorDetail ?? string.Empty))
                            .OrderBy(e => e.TimestampUtc)
                            .ToList();

                        if (completionEvents.Count > 0)
                        {
                            posTimes[position].Add(completionEvents[0].TimestampUtc);
                            var sentInWindow = CountSentInWindow(
                                sent,
                                accountName,
                                name,
                                windowStartUtc,
                                windowEndUtc);
                            posLeads[position].Add(sentInWindow > 0
                                ? sentInWindow.ToString(CultureInfo.InvariantCulture)
                                : "—");
                        }
                        else
                        {
                            posTimes[position].Add(null);
                            posLeads[position].Add(null);
                        }

                        foreach (var error in errors)
                        {
                            posErrors[position].Add(error);
                        }
                    }
                }

                var totalPositions = posToName.Keys.DefaultIfEmpty(10).Max();
                foreach (var position in posToName.Keys.OrderBy(x => x))
                {
                    if (lastCyclePositions.Contains(position))
                    {
                        continue;
                    }

                    posTimes[position].Add(null);
                    posLeads[position].Add(null);
                    var lastCycleStartUtc = cycles[^1][0].TimestampUtc;
                    var hasMatchingError = posErrors[position]
                        .Any(e => e.TimestampUtc == lastCycleStartUtc);
                    if (!hasMatchingError)
                    {
                        posErrors[position].Add(new MonitoringCycleErrorDto(
                            lastCycleStartUtc,
                            "Не запущен (прервано до очереди)"));
                    }
                }

                var notStarted = posToName.Keys
                    .Where(position => posTimes[position].All(t => t is null))
                    .OrderBy(x => x)
                    .ToList();

                if (notStarted.Count > 0)
                {
                    accountsWithNotStarted++;
                    totalNotStartedPositions += notStarted.Count;
                    var names = string.Join(", ", notStarted.Select(p => $"{p}/{totalPositions} ({posToName[p]})"));
                    notStartedSummaries.Add($"  {accountName}: {notStarted.Count} не запущены — {names}");
                }

                var leadTotal = CountSentForAccountOnDay(sent, accountName, day);
                var leadParts = new List<string>();
                foreach (var position in posToName.Keys.OrderBy(x => x))
                {
                    var positionLeadTotal = CountSentForSubProfileOnDay(
                        sent,
                        accountName,
                        posToName[position],
                        day);
                    if (positionLeadTotal > 0)
                    {
                        leadParts.Add($"{position}/{totalPositions} ({posToName[position]}) = {positionLeadTotal}");
                    }
                }

                leadSummaries.Add(new MonitoringCycleLeadSummaryDto(
                    accountName,
                    leadTotal,
                    leadParts));

                if (!isDetailed)
                {
                    continue;
                }

                var rows = posToName.Keys
                    .OrderBy(x => x)
                    .Select(position =>
                    {
                        var times = posTimes[position].Where(t => t is not null).Select(t => t!.Value).ToList();
                        var leads = posLeads[position].Where(v => v is not null).Select(v => v!).ToList();
                        var errors = posErrors[position]
                            .GroupBy(e => (e.TimestampUtc, e.Detail))
                            .Select(g => g.First())
                            .OrderBy(e => e.TimestampUtc)
                            .ToList();
                        return new MonitoringCycleSubProfileRowDto(
                            position,
                            totalPositions,
                            posToName[position],
                            times,
                            leads,
                            errors);
                    })
                    .ToList();

                var dateUtc = LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(day).UtcStartInclusive;
                reports.Add(new MonitoringCycleAccountReportDto(
                    accountName,
                    dateUtc,
                    posToName.Count,
                    cycleCount,
                    leadTotal,
                    rows,
                    notStarted.Select(p => $"{p}/{totalPositions} ({posToName[p]})").ToList()));
            }
        }

        var mergedLeadSummaries = leadSummaries
            .GroupBy(x => x.AccountName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MonitoringCycleLeadSummaryDto(
                g.Key,
                g.Sum(x => x.TotalLeads),
                g.SelectMany(x => x.Breakdown).Distinct(StringComparer.Ordinal).ToList()))
            .OrderBy(x => ExtractAccountSortKey(x.AccountName))
            .ToList();

        return new MonitoringCycleReportDto(
            isDetailed,
            mergedLeadSummaries.Sum(x => x.TotalLeads),
            accountsWithNotStarted,
            totalNotStartedPositions,
            notStartedSummaries,
            mergedLeadSummaries,
            isDetailed
                ? reports.OrderBy(x => ExtractAccountSortKey(x.AccountName)).ToList()
                : []);
    }

    private static MonitoringCycleReportDto Empty(bool isDetailed) =>
        new(isDetailed, 0, 0, 0, [], [], []);

    private static IEnumerable<DateTime> EnumerateDays(DateTime startLocal, DateTime endLocal)
    {
        for (var day = startLocal.Date; day <= endLocal.Date; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    private static List<MonitoringCycleLogEvent> ParseEvents(
        IEnumerable<(DateTime TimestampUtc, string Message, string? PropertiesJson)> logRows,
        IReadOnlySet<string>? allowedAccountNames)
    {
        var events = new List<MonitoringCycleLogEvent>();
        foreach (var (timestampUtc, message, propertiesJson) in logRows)
        {
            if (TryParseFromStructuredJson(timestampUtc, message, propertiesJson, out var structured))
            {
                if (allowedAccountNames is null
                    || allowedAccountNames.Contains(structured.AccountName))
                {
                    events.Add(structured);
                }

                continue;
            }

            if (TryParseFromMessage(timestampUtc, message, out structured)
                && (allowedAccountNames is null || allowedAccountNames.Contains(structured.AccountName)))
            {
                events.Add(structured);
            }
        }

        return events
            .OrderBy(e => e.TimestampUtc)
            .ToList();
    }

    private static bool TryParseFromStructuredJson(
        DateTime timestampUtc,
        string message,
        string? propertiesJson,
        out MonitoringCycleLogEvent parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(propertiesJson) || !propertiesJson.TrimStart().StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(propertiesJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("context", out var context))
            {
                return false;
            }

            var accountName = TryReadContextString(context, "accountName");
            if (string.IsNullOrWhiteSpace(accountName))
            {
                return false;
            }

            var logMessage = root.TryGetProperty("message", out var messageNode)
                ? messageNode.GetString() ?? message
                : message;
            var subProfileName = TryReadContextString(context, "subProfile.name") ?? string.Empty;
            var subProfileIndex = TryReadContextInt(context, "subProfile.index");
            var subProfileTotal = TryReadContextInt(context, "subProfile.total");
            if (subProfileTotal <= 0)
            {
                subProfileTotal = 10;
            }

            var errorType = TryReadContextString(context, "error.type") ?? string.Empty;

            if (SwitchMarkerRegex().IsMatch(logMessage) || NewWorkerSwitchMarkerRegex().IsMatch(logMessage))
            {
                parsed = new MonitoringCycleLogEvent(
                    accountName,
                    timestampUtc,
                    MonitoringCycleLogEventType.Switch,
                    subProfileName,
                    subProfileIndex > 0 ? subProfileIndex : ExtractPositionFromMessage(logMessage),
                    subProfileTotal,
                    null,
                    null);
                return true;
            }

            if (logMessage.Contains("— отклики:", StringComparison.Ordinal)
                && logMessage.Contains("новых к публикации", StringComparison.Ordinal))
            {
                parsed = new MonitoringCycleLogEvent(
                    accountName,
                    timestampUtc,
                    MonitoringCycleLogEventType.Stats,
                    subProfileName,
                    subProfileIndex,
                    subProfileTotal,
                    null,
                    null);
                return true;
            }

            var publishedMatch = PublishedFromMessageRegex().Match(logMessage);
            if (publishedMatch.Success
                && int.TryParse(publishedMatch.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var publishedCount))
            {
                parsed = new MonitoringCycleLogEvent(
                    publishedMatch.Groups[1].Value.Trim(),
                    timestampUtc,
                    MonitoringCycleLogEventType.Completion,
                    publishedMatch.Groups[2].Value.Trim(),
                    subProfileIndex,
                    subProfileTotal,
                    publishedCount,
                    null);
                return true;
            }

            if (logMessage.Contains("новых для LeadFlow", StringComparison.OrdinalIgnoreCase))
            {
                int? leads = TryParseProcessedNowCount(logMessage, out var processedNow)
                    ? processedNow
                    : null;
                parsed = new MonitoringCycleLogEvent(
                    accountName,
                    timestampUtc,
                    MonitoringCycleLogEventType.Completion,
                    subProfileName,
                    subProfileIndex,
                    subProfileTotal,
                    leads,
                    null);
                return true;
            }

            if (logMessage.Contains("(объявления)", StringComparison.OrdinalIgnoreCase)
                && logMessage.Contains("активных", StringComparison.OrdinalIgnoreCase))
            {
                parsed = new MonitoringCycleLogEvent(
                    accountName,
                    timestampUtc,
                    MonitoringCycleLogEventType.Stats,
                    subProfileName,
                    subProfileIndex,
                    subProfileTotal,
                    null,
                    null);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(errorType)
                && (logMessage.Contains("не удался", StringComparison.OrdinalIgnoreCase)
                    || logMessage.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                    || logMessage.Contains("ERR_TIMED_OUT", StringComparison.OrdinalIgnoreCase)
                    || logMessage.Contains("Не удалось обработать суб-профиль", StringComparison.OrdinalIgnoreCase)))
            {
                var detail = errorType.Contains('.', StringComparison.Ordinal)
                    ? errorType.Split('.')[^1]
                    : errorType;
                parsed = new MonitoringCycleLogEvent(
                    accountName,
                    timestampUtc,
                    MonitoringCycleLogEventType.Error,
                    subProfileName,
                    subProfileIndex,
                    subProfileTotal,
                    null,
                    detail);
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TryParseFromMessage(
        DateTime timestampUtc,
        string message,
        out MonitoringCycleLogEvent parsed)
    {
        parsed = null!;

        var switchMatch = SwitchFromMessageRegex().Match(message);
        if (switchMatch.Success)
        {
            parsed = new MonitoringCycleLogEvent(
                switchMatch.Groups[3].Value.Trim(),
                timestampUtc,
                MonitoringCycleLogEventType.Switch,
                switchMatch.Groups[4].Value.Trim(),
                int.Parse(switchMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(switchMatch.Groups[2].Value, CultureInfo.InvariantCulture),
                null,
                null);
            return true;
        }

        var newSwitchMatch = NewWorkerSwitchRegex().Match(message);
        if (newSwitchMatch.Success)
        {
            parsed = new MonitoringCycleLogEvent(
                newSwitchMatch.Groups[1].Value.Trim(),
                timestampUtc,
                MonitoringCycleLogEventType.Switch,
                newSwitchMatch.Groups[2].Value.Trim(),
                int.Parse(newSwitchMatch.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(newSwitchMatch.Groups[4].Value, CultureInfo.InvariantCulture),
                null,
                null);
            return true;
        }

        var publishedMatch = PublishedFromMessageRegex().Match(message);
        if (publishedMatch.Success)
        {
            parsed = new MonitoringCycleLogEvent(
                publishedMatch.Groups[1].Value.Trim(),
                timestampUtc,
                MonitoringCycleLogEventType.Completion,
                publishedMatch.Groups[2].Value.Trim(),
                0,
                10,
                int.Parse(publishedMatch.Groups[3].Value, CultureInfo.InvariantCulture),
                null);
            return true;
        }

        var newExtractionMarkerMatch = NewWorkerExtractionMarkerRegex().Match(message);
        if (newExtractionMarkerMatch.Success)
        {
            parsed = new MonitoringCycleLogEvent(
                newExtractionMarkerMatch.Groups[1].Value.Trim(),
                timestampUtc,
                MonitoringCycleLogEventType.Stats,
                newExtractionMarkerMatch.Groups[2].Value.Trim(),
                0,
                10,
                null,
                null);
            return true;
        }

        var completionMatch = CompletionFromMessageRegex().Match(message);
        if (completionMatch.Success)
        {
            int? leads = TryParseProcessedNowCount(message, out var processedNow)
                ? processedNow
                : int.Parse(completionMatch.Groups[3].Value, CultureInfo.InvariantCulture);
            parsed = new MonitoringCycleLogEvent(
                completionMatch.Groups[2].Value.Trim(),
                timestampUtc,
                MonitoringCycleLogEventType.Completion,
                completionMatch.Groups[1].Value.Trim(),
                0,
                10,
                leads,
                null);
            return true;
        }

        var statsMatch = StatsFromMessageRegex().Match(message);
        if (statsMatch.Success)
        {
            parsed = new MonitoringCycleLogEvent(
                ExtractAccountNameFromStatsMessage(message) ?? string.Empty,
                timestampUtc,
                MonitoringCycleLogEventType.Stats,
                statsMatch.Groups[1].Value.Trim(),
                0,
                10,
                null,
                null);
            return !string.IsNullOrWhiteSpace(parsed.AccountName);
        }

        var errorMatch = ErrorFromMessageRegex().Match(message);
        if (errorMatch.Success)
        {
            parsed = BuildErrorEvent(
                errorMatch.Groups[2].Value.Trim(),
                timestampUtc,
                errorMatch.Groups[1].Value.Trim(),
                errorMatch.Groups[3].Value.Trim());
            return true;
        }

        var newIssueMatch = NewWorkerSubProfileIssueRegex().Match(message);
        if (newIssueMatch.Success)
        {
            parsed = BuildErrorEvent(
                newIssueMatch.Groups[1].Value.Trim(),
                timestampUtc,
                newIssueMatch.Groups[2].Value.Trim(),
                newIssueMatch.Groups[3].Value.Trim());
            return true;
        }

        var newSwitchFailedMatch = NewWorkerSwitchFailedRegex().Match(message);
        if (newSwitchFailedMatch.Success)
        {
            parsed = BuildErrorEvent(
                newSwitchFailedMatch.Groups[1].Value.Trim(),
                timestampUtc,
                newSwitchFailedMatch.Groups[2].Value.Trim(),
                newSwitchFailedMatch.Groups[3].Value.Trim());
            return true;
        }

        return false;
    }

    private static MonitoringCycleLogEvent BuildErrorEvent(
        string accountName,
        DateTime timestampUtc,
        string subProfileName,
        string detail)
    {
        detail = detail.Trim();
        const string blockingSuffix = " Дальнейший обход аккаунта остановлен.";
        if (detail.EndsWith(blockingSuffix, StringComparison.Ordinal))
        {
            detail = detail[..^blockingSuffix.Length].Trim();
        }

        if (detail.Length > 80)
        {
            detail = detail[..80];
        }

        return new MonitoringCycleLogEvent(
            accountName,
            timestampUtc,
            MonitoringCycleLogEventType.Error,
            subProfileName,
            0,
            10,
            null,
            detail);
    }

    private static bool TryParseProcessedNowCount(string message, out int processedNow)
    {
        processedNow = 0;
        var match = ProcessedNowFromMessageRegex().Match(message);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out processedNow);
    }

    private static string? ExtractAccountNameFromStatsMessage(string message)
    {
        var completionMatch = CompletionFromMessageRegex().Match(message);
        if (completionMatch.Success)
        {
            return completionMatch.Groups[2].Value.Trim();
        }

        var errorMatch = ErrorFromMessageRegex().Match(message);
        return errorMatch.Success ? errorMatch.Groups[2].Value.Trim() : null;
    }

    private static int ExtractPositionFromMessage(string message)
    {
        var match = PositionFromMessageRegex().Match(message);
        return match.Success && int.TryParse(match.Groups[1].Value, out var position)
            ? position
            : 0;
    }

    private static List<List<MonitoringCycleLogEvent>> SplitCycles(IReadOnlyList<MonitoringCycleLogEvent> switches)
    {
        var cycles = new List<List<MonitoringCycleLogEvent>>();
        var current = new List<MonitoringCycleLogEvent>();
        foreach (var sw in switches)
        {
            if (sw.SubProfileIndex == 1 && current.Count > 0)
            {
                cycles.Add(current);
                current = [];
            }

            current.Add(sw);
        }

        if (current.Count > 0)
        {
            cycles.Add(current);
        }

        return cycles;
    }

    private static int CountSentInWindow(
        IReadOnlyList<MonitoringCycleSentResponse> sentResponses,
        string accountName,
        string subProfileName,
        DateTime windowStartUtc,
        DateTime windowEndUtc) =>
        sentResponses.Count(response =>
            string.Equals(response.AccountName, accountName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(response.SubProfileName, subProfileName, StringComparison.OrdinalIgnoreCase)
            && response.TimestampUtc >= windowStartUtc
            && response.TimestampUtc < windowEndUtc);

    private static int CountSentForAccountOnDay(
        IReadOnlyList<MonitoringCycleSentResponse> sentResponses,
        string accountName,
        DateTime day)
    {
        var (utcStart, utcEnd) = LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(day);
        return sentResponses.Count(response =>
            string.Equals(response.AccountName, accountName, StringComparison.OrdinalIgnoreCase)
            && response.TimestampUtc >= utcStart
            && response.TimestampUtc < utcEnd);
    }

    private static int CountSentForSubProfileOnDay(
        IReadOnlyList<MonitoringCycleSentResponse> sentResponses,
        string accountName,
        string subProfileName,
        DateTime day)
    {
        var (utcStart, utcEnd) = LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(day);
        return sentResponses.Count(response =>
            string.Equals(response.AccountName, accountName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(response.SubProfileName, subProfileName, StringComparison.OrdinalIgnoreCase)
            && response.TimestampUtc >= utcStart
            && response.TimestampUtc < utcEnd);
    }

    private static int ExtractAccountSortKey(string accountName)
    {
        var match = Regex.Match(accountName, @"\d+");
        return match.Success && int.TryParse(match.Value, out var number) ? number : int.MaxValue;
    }

    private static string? TryReadContextString(JsonElement context, string key)
    {
        if (!context.TryGetProperty(key, out var node))
        {
            return null;
        }

        return node.ValueKind == JsonValueKind.String ? node.GetString() : node.ToString();
    }

    private static int TryReadContextInt(JsonElement context, string key)
    {
        if (!context.TryGetProperty(key, out var node))
        {
            return 0;
        }

        return node.ValueKind switch
        {
            JsonValueKind.Number when node.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(node.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }
}
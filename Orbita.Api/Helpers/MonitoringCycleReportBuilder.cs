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
    DateTime TimestampUtc,
    string SubProfileId = "");

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
    int PublishedCount,
    int FoundCount = 0,
    int CollectedCount = 0,
    int CaptchaCount = 0,
    int CaptchaSolvedCount = 0);

/// <summary>Enabled subprofiles of an account (panel order) for a full day matrix.</summary>
internal sealed record MonitoringAccountSubProfileCatalogEntry(
    int Position,
    string Id,
    string Name);

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
    /// Полный отчёт только из типизированного журнала запусков.
    /// «Успешное завершение» = проход субпрофиля без ошибки (Outcome=Completed), не факт отправки лида.
    /// «Откликов» — сколько новых откликов собрано в проходе (CandidateResponses.CollectedAt в окне прохода).
    /// Если collected не передан, берётся CollectedCount журнала, иначе PublishedCount (legacy).
    /// Строки — все включённые субпрофили аккаунта (каталог), журнал накладывается поверх.
    /// </summary>
    public static MonitoringCycleReportDto BuildFromJournal(
        IReadOnlyList<MonitoringCycleRunSnapshot> cycles,
        DateTime startLocal,
        DateTime endLocal,
        IReadOnlySet<string>? allowedAccountNames = null,
        IReadOnlyList<MonitoringCycleSentResponse>? sentResponses = null,
        IReadOnlyDictionary<string, IReadOnlyList<MonitoringAccountSubProfileCatalogEntry>>? accountCatalog = null,
        IReadOnlyDictionary<string, string?>? accountLastErrors = null)
    {
        const bool isDetailed = true;
        var catalog = accountCatalog
            ?? new Dictionary<string, IReadOnlyList<MonitoringAccountSubProfileCatalogEntry>>(StringComparer.OrdinalIgnoreCase);
        var lastErrors = accountLastErrors
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
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
            return Empty(isDetailed: false);
        }

        var reports = new List<MonitoringCycleAccountReportDto>();
        var notStartedSummaries = new List<string>();
        var leadSummaries = new List<MonitoringCycleLeadSummaryDto>();
        var totalNotStartedPositions = 0;
        var accountsWithNotStarted = 0;
        var remainingCollected = sentResponses?.ToList();
        var reportCaptcha = 0;
        var reportCaptchaSolved = 0;

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
                catalog.TryGetValue(accountName, out var accountSubs);
                accountSubs ??= [];

                // Row identity: catalog first (full list of enabled subs), then any journal-only names.
                var rowsMeta = BuildAccountDayRows(accountSubs, accountCycles);
                var totalPositions = rowsMeta.Count;
                var posToName = rowsMeta.ToDictionary(r => r.Position, r => r.Name);
                var posTimes = rowsMeta.ToDictionary(r => r.Position, _ => new List<DateTime?>());
                var posResponses = rowsMeta.ToDictionary(r => r.Position, _ => new List<string?>());
                var posCaptcha = rowsMeta.ToDictionary(r => r.Position, _ => new List<MonitoringCycleCaptchaDto>());
                var posErrors = rowsMeta.ToDictionary(r => r.Position, _ => new List<MonitoringCycleErrorDto>());
                var posPasses = rowsMeta.ToDictionary(r => r.Position, _ => new List<MonitoringCyclePassDto>());
                var posHadRun = rowsMeta.ToDictionary(r => r.Position, _ => false);
                var posLeadTotals = rowsMeta.ToDictionary(r => r.Position, _ => 0);
                var posExplicitNotStarted = new HashSet<int>();
                var accountCaptcha = 0;
                var accountCaptchaSolved = 0;

                for (var cycleIndex = 0; cycleIndex < accountCycles.Count; cycleIndex++)
                {
                    var cycle = accountCycles[cycleIndex];
                    var isLastCycle = cycleIndex == accountCycles.Count - 1;
                    var cycleInterrupted = isLastCycle
                        && cycle.Status is MonitoringCycleRunStatuses.Aborted
                            or MonitoringCycleRunStatuses.Failed
                            or MonitoringCycleRunStatuses.Running;

                    var runsByRow = new Dictionary<int, MonitoringSubProfileRunSnapshot>();
                    foreach (var sp in cycle.SubProfiles.OrderBy(x => x.StartedAtUtc))
                    {
                        if (IsCycleLevelRun(sp))
                        {
                            continue;
                        }

                        var rowPos = MatchRunToRowPosition(sp, rowsMeta);
                        if (rowPos is null)
                        {
                            continue;
                        }

                        runsByRow[rowPos.Value] = sp;
                    }

                    foreach (var row in rowsMeta)
                    {
                        var position = row.Position;
                        if (!runsByRow.TryGetValue(position, out var run))
                        {
                            posTimes[position].Add(null);
                            posResponses[position].Add(null);
                            // A subprofile is "not started" when the last interrupted cycle
                            // never reached it — including cycles that died before any sub.
                            if (cycleInterrupted && !posHadRun[position])
                            {
                                posExplicitNotStarted.Add(position);
                            }

                            continue;
                        }

                        if (run.Outcome == MonitoringSubProfileRunOutcomes.Skipped)
                        {
                            posPasses[position].Add(BuildPass(cycle, run, passLeads: 0, captchaSeen: 0, captchaSolved: 0));
                            if (cycleInterrupted && !posHadRun[position])
                            {
                                posExplicitNotStarted.Add(position);
                            }

                            continue;
                        }

                        posHadRun[position] = true;
                        posToName[position] = string.IsNullOrWhiteSpace(run.SubProfileName)
                            ? posToName[position]
                            : run.SubProfileName.Trim();

                        var passLeads = CountCollectedForRun(
                            accountName,
                            cycle,
                            run,
                            remainingCollected);
                        var (captchaSeen, captchaSolved) = CaptchaForRun(run);
                        accountCaptcha += captchaSeen;
                        accountCaptchaSolved += captchaSolved;
                        var pass = BuildPass(cycle, run, passLeads, captchaSeen, captchaSolved);
                        posPasses[position].Add(pass);
                        if (pass.CaptchaStatus is { Length: > 0 } captchaStatus)
                        {
                            posCaptcha[position].Add(new MonitoringCycleCaptchaDto(
                                pass.TimestampUtc,
                                captchaStatus,
                                Unsolved: pass.CaptchaUnsolved));
                        }

                        if (pass.Completed)
                        {
                            posTimes[position].Add(pass.TimestampUtc);
                            posResponses[position].Add(passLeads.ToString(CultureInfo.InvariantCulture));
                            posLeadTotals[position] += passLeads;
                        }
                        else if (!pass.InProgress)
                        {
                            posTimes[position].Add(null);
                            posResponses[position].Add(pass.HasCollected
                                ? passLeads.ToString(CultureInfo.InvariantCulture)
                                : null);
                            posLeadTotals[position] += passLeads;
                            if (pass.ErrorDetail is { Length: > 0 } detail)
                            {
                                posErrors[position].Add(new MonitoringCycleErrorDto(
                                    pass.TimestampUtc,
                                    detail));
                            }
                        }
                        else
                        {
                            posTimes[position].Add(null);
                            posResponses[position].Add(null);
                        }
                    }
                }

                var notStarted = posToName.Keys
                    .Where(position =>
                        posExplicitNotStarted.Contains(position)
                        && posTimes.TryGetValue(position, out var times)
                        && times.All(t => t is null))
                    .OrderBy(x => x)
                    .ToList();

                if (notStarted.Count > 0)
                {
                    accountsWithNotStarted++;
                    totalNotStartedPositions += notStarted.Count;
                    var names = string.Join(", ", notStarted.Select(p => $"{p}/{totalPositions} ({posToName[p]})"));
                    notStartedSummaries.Add($"  {accountName}: {notStarted.Count} не запущены — {names}");
                }

                var leadTotal = posLeadTotals.Values.Sum();
                var leadParts = new List<string>();
                foreach (var position in posToName.Keys.OrderBy(x => x))
                {
                    var positionResponseTotal = posLeadTotals[position];
                    if (positionResponseTotal > 0)
                    {
                        leadParts.Add($"{position}/{totalPositions} ({posToName[position]}) = {positionResponseTotal}");
                    }
                }

                leadSummaries.Add(new MonitoringCycleLeadSummaryDto(
                    accountName,
                    leadTotal,
                    leadParts));
                reportCaptcha += accountCaptcha;
                reportCaptchaSolved += accountCaptchaSolved;
                lastErrors.TryGetValue(accountName, out var accountError);

                var tableRows = posToName.Keys
                    .OrderBy(x => x)
                    .Select(position =>
                    {
                        var times = posTimes[position].Where(t => t is not null).Select(t => t!.Value).ToList();
                        var leads = posResponses[position].Where(v => v is not null).Select(v => v!).ToList();
                        var captcha = posCaptcha[position]
                            .GroupBy(c => (c.TimestampUtc, c.Status))
                            .Select(g => g.First())
                            .OrderBy(c => c.TimestampUtc)
                            .ToList();
                        var errors = posErrors[position]
                            .GroupBy(e => (e.TimestampUtc, e.Detail))
                            .Select(g => g.First())
                            .OrderBy(e => e.TimestampUtc)
                            .ToList();
                        var passes = posPasses[position]
                            .GroupBy(p => (p.TimestampUtc, p.Completed, p.CaptchaStatus, p.ErrorDetail, p.InProgress, p.Skipped))
                            .Select(g => g.First())
                            .OrderBy(p => p.TimestampUtc)
                            .ToList();
                        var skipInfo = InferNotStarted(
                            accountCycles,
                            position,
                            rowsMeta,
                            posHadRun[position],
                            accountError);
                        return new MonitoringCycleSubProfileRowDto(
                            position,
                            totalPositions,
                            posToName[position],
                            times,
                            leads,
                            errors,
                            WasStarted: posHadRun[position],
                            CaptchaPerCycle: captcha,
                            Passes: passes,
                            NotStartedReason: skipInfo?.Reason,
                            NotStartedAtUtc: skipInfo?.At);
                    })
                    .ToList();

                var dateUtc = LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(day).UtcStartInclusive;
                reports.Add(new MonitoringCycleAccountReportDto(
                    accountName,
                    dateUtc,
                    totalPositions,
                    accountCycles.Count,
                    leadTotal,
                    tableRows,
                    notStarted.Select(p => $"{p}/{totalPositions} ({posToName[p]})").ToList(),
                    accountCaptcha,
                    accountCaptchaSolved));
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
            reports
                .OrderBy(x => x.DateUtc)
                .ThenBy(x => ExtractAccountSortKey(x.AccountName))
                .ToList(),
            reportCaptcha,
            reportCaptchaSolved);
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

    internal static MonitoringCycleReportDto Empty(bool isDetailed) =>
        new(isDetailed, 0, 0, 0, [], [], []);

    private static IEnumerable<DateTime> EnumerateDays(DateTime startLocal, DateTime endLocal)
    {
        for (var day = startLocal.Date; day <= endLocal.Date; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    private sealed record AccountDayRow(int Position, string Id, string Name);

    /// <summary>
    /// Catalog (enabled subs) first — full matrix like the worker page.
    /// Journal-only names appended if missing from catalog.
    /// </summary>
    private static List<AccountDayRow> BuildAccountDayRows(
        IReadOnlyList<MonitoringAccountSubProfileCatalogEntry> catalog,
        IReadOnlyList<MonitoringCycleRunSnapshot> accountCycles)
    {
        var rows = new List<AccountDayRow>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in catalog.OrderBy(x => x.Position))
        {
            var name = string.IsNullOrWhiteSpace(entry.Name) ? "—" : entry.Name.Trim();
            var id = entry.Id?.Trim() ?? string.Empty;
            rows.Add(new AccountDayRow(rows.Count + 1, id, name));
            if (!string.IsNullOrWhiteSpace(id))
            {
                usedIds.Add(id);
            }

            usedNames.Add(name);
        }

        foreach (var cycle in accountCycles)
        {
            foreach (var sp in cycle.SubProfiles.OrderBy(x => x.Position).ThenBy(x => x.StartedAtUtc))
            {
                if (IsCycleLevelRun(sp))
                {
                    continue;
                }

                var id = sp.SubProfileId?.Trim() ?? string.Empty;
                var name = string.IsNullOrWhiteSpace(sp.SubProfileName) ? "—" : sp.SubProfileName.Trim();
                if ((!string.IsNullOrWhiteSpace(id) && usedIds.Contains(id))
                    || usedNames.Contains(name))
                {
                    continue;
                }

                rows.Add(new AccountDayRow(rows.Count + 1, id, name));
                if (!string.IsNullOrWhiteSpace(id))
                {
                    usedIds.Add(id);
                }

                usedNames.Add(name);
            }
        }

        if (rows.Count == 0)
        {
            // Absolute fallback: journal positions only.
            foreach (var sp in accountCycles.SelectMany(c => c.SubProfiles).OrderBy(x => x.Position))
            {
                var name = string.IsNullOrWhiteSpace(sp.SubProfileName) ? $"#{sp.Position}" : sp.SubProfileName.Trim();
                if (rows.Any(r => r.Position == sp.Position))
                {
                    continue;
                }

                rows.Add(new AccountDayRow(sp.Position, sp.SubProfileId?.Trim() ?? string.Empty, name));
            }

            rows = rows.OrderBy(r => r.Position).ToList();
            // Re-number densely 1..n for display consistency.
            rows = rows.Select((r, i) => r with { Position = i + 1 }).ToList();
        }

        return rows;
    }

    private static int CountCollectedForRun(
        string accountName,
        MonitoringCycleRunSnapshot cycle,
        MonitoringSubProfileRunSnapshot run,
        List<MonitoringCycleSentResponse>? remainingCollected)
    {
        if (run.Outcome is not MonitoringSubProfileRunOutcomes.Completed
            and not MonitoringSubProfileRunOutcomes.Failed)
        {
            return 0;
        }

        if (remainingCollected is not null)
        {
            var nextStart = cycle.SubProfiles
                .Where(x => x.StartedAtUtc > run.StartedAtUtc)
                .Select(x => (DateTime?)x.StartedAtUtc)
                .OrderBy(x => x)
                .FirstOrDefault();
            var windowEnd = run.CompletedAtUtc
                ?? cycle.FinishedAtUtc
                ?? DateTime.MaxValue;
            var matches = remainingCollected
                .Where(x =>
                    string.Equals(x.AccountName, accountName, StringComparison.OrdinalIgnoreCase)
                    && MatchesCollectedSubProfile(x, run)
                    && x.TimestampUtc >= run.StartedAtUtc
                    && x.TimestampUtc <= windowEnd
                    && (nextStart is null
                        || x.TimestampUtc < nextStart.Value
                        || (run.CompletedAtUtc is DateTime completed && x.TimestampUtc <= completed)))
                .ToList();
            foreach (var match in matches)
            {
                remainingCollected.Remove(match);
            }

            return matches.Count;
        }

        return run.CollectedCount > 0 ? run.CollectedCount : run.PublishedCount;
    }

    private static bool MatchesCollectedSubProfile(
        MonitoringCycleSentResponse collected,
        MonitoringSubProfileRunSnapshot run)
    {
        var collectedId = collected.SubProfileId?.Trim() ?? string.Empty;
        var runId = run.SubProfileId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(collectedId) && !string.IsNullOrWhiteSpace(runId))
        {
            return string.Equals(collectedId, runId, StringComparison.OrdinalIgnoreCase);
        }

        var collectedName = string.IsNullOrWhiteSpace(collected.SubProfileName)
            ? "—"
            : collected.SubProfileName.Trim();
        var runName = string.IsNullOrWhiteSpace(run.SubProfileName)
            ? "—"
            : run.SubProfileName.Trim();
        return string.Equals(collectedName, runName, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Seen, int Solved) CaptchaForRun(MonitoringSubProfileRunSnapshot run)
    {
        var seen = Math.Max(0, run.CaptchaCount);
        var solved = Math.Min(seen, Math.Max(0, run.CaptchaSolvedCount));
        if (seen == 0
            && run.Outcome == MonitoringSubProfileRunOutcomes.Failed
            && string.Equals(run.ErrorType, "captcha", StringComparison.OrdinalIgnoreCase))
        {
            seen = 1;
        }

        return (seen, solved);
    }

    private static MonitoringCyclePassDto BuildPass(
        MonitoringCycleRunSnapshot cycle,
        MonitoringSubProfileRunSnapshot run,
        int passLeads,
        int captchaSeen,
        int captchaSolved)
    {
        var timestamp = run.CompletedAtUtc ?? run.StartedAtUtc;
        var captchaStatus = captchaSeen > 0 ? FormatCaptchaPass(captchaSeen, captchaSolved) : null;
        var captchaUnsolved = captchaSeen > 0 && captchaSolved < captchaSeen;
        var loginRequired = string.Equals(run.ErrorType, "auth-required", StringComparison.OrdinalIgnoreCase);
        if (run.Outcome == MonitoringSubProfileRunOutcomes.Completed
            && run.CompletedAtUtc is DateTime)
        {
            return new MonitoringCyclePassDto(
                timestamp,
                Completed: true,
                CollectedCount: passLeads,
                HasCollected: true,
                CaptchaStatus: captchaStatus,
                CaptchaUnsolved: captchaUnsolved);
        }

        if (run.Outcome == MonitoringSubProfileRunOutcomes.Skipped)
        {
            var skipDetail = !string.IsNullOrWhiteSpace(run.ErrorMessage)
                ? run.ErrorMessage!
                : "очередь не дошла";
            if (skipDetail.Length > 80)
            {
                skipDetail = skipDetail[..80];
            }

            return new MonitoringCyclePassDto(
                timestamp,
                Completed: false,
                CollectedCount: 0,
                HasCollected: false,
                ErrorDetail: skipDetail,
                Skipped: true);
        }

        if (run.Outcome == MonitoringSubProfileRunOutcomes.Failed)
        {
            var detail = !string.IsNullOrWhiteSpace(run.ErrorMessage)
                ? run.ErrorMessage!
                : !string.IsNullOrWhiteSpace(run.ErrorType)
                    ? run.ErrorType!
                    : "Ошибка прохода";
            if (detail.Length > 80)
            {
                detail = detail[..80];
            }

            return new MonitoringCyclePassDto(
                timestamp,
                Completed: false,
                CollectedCount: passLeads,
                HasCollected: passLeads > 0,
                CaptchaStatus: captchaStatus,
                CaptchaUnsolved: captchaUnsolved,
                ErrorDetail: detail,
                LoginRequired: loginRequired);
        }

        if (cycle.Status != MonitoringCycleRunStatuses.Running)
        {
            return new MonitoringCyclePassDto(
                timestamp,
                Completed: false,
                CollectedCount: passLeads,
                HasCollected: passLeads > 0,
                CaptchaStatus: captchaStatus,
                CaptchaUnsolved: captchaUnsolved,
                ErrorDetail: "цикл прерван до завершения субпрофиля");
        }

        return new MonitoringCyclePassDto(
            timestamp,
            Completed: false,
            CollectedCount: 0,
            HasCollected: false,
            CaptchaStatus: captchaStatus,
            CaptchaUnsolved: captchaUnsolved,
            InProgress: true);
    }

    internal static string FormatCaptchaPass(int seen, int solved)
    {
        if (seen <= 0)
        {
            return "—";
        }

        if (solved >= seen)
        {
            return seen == 1 ? "решена" : $"{seen} решены";
        }

        if (solved <= 0)
        {
            return seen == 1 ? "не решена" : $"{seen} не решены";
        }

        return $"решено {solved}/{seen}";
    }

    private readonly record struct NotStartedInfo(string Reason, DateTime At);

    private static NotStartedInfo? InferNotStarted(
        IReadOnlyList<MonitoringCycleRunSnapshot> accountCycles,
        int position,
        IReadOnlyList<AccountDayRow> rowsMeta,
        bool wasStarted,
        string? accountLastError = null)
    {
        if (wasStarted)
        {
            return null;
        }

        MonitoringCycleRunSnapshot? lastMiss = null;
        foreach (var cycle in accountCycles)
        {
            var reached = cycle.SubProfiles.Any(sp =>
                !IsCycleLevelRun(sp)
                && sp.Outcome != MonitoringSubProfileRunOutcomes.Skipped
                && MatchRunToRowPosition(sp, rowsMeta) == position);
            if (!reached)
            {
                lastMiss = cycle;
            }
        }

        if (lastMiss is null)
        {
            return null;
        }

        var at = CycleStopTimestamp(lastMiss);
        var diedBeforeQueue = !lastMiss.SubProfiles.Any(sp => !IsCycleLevelRun(sp));
        if (diedBeforeQueue)
        {
            return new NotStartedInfo(DescribeEmptyCycleStop(lastMiss, accountLastError), at);
        }

        var interrupted = lastMiss.Status is MonitoringCycleRunStatuses.Aborted
            or MonitoringCycleRunStatuses.Failed
            or MonitoringCycleRunStatuses.Running;
        if (interrupted)
        {
            var queuedTotal = lastMiss.SubProfiles
                .Where(sp => !IsCycleLevelRun(sp))
                .Select(sp => sp.Total)
                .DefaultIfEmpty(0)
                .Max();
            if (queuedTotal > 0 && queuedTotal < rowsMeta.Count)
            {
                return new NotStartedInfo("не входил в очередь последней попытки", at);
            }

            return new NotStartedInfo($"очередь не дошла: {DescribeCycleStop(lastMiss)}", at);
        }

        return new NotStartedInfo("не попал в проходы за день", at);
    }

    private static bool IsCycleLevelRun(MonitoringSubProfileRunSnapshot run) =>
        string.IsNullOrWhiteSpace(run.SubProfileId)
        && (string.IsNullOrWhiteSpace(run.SubProfileName) || run.SubProfileName.Trim() == "—");

    private static string DescribeEmptyCycleStop(MonitoringCycleRunSnapshot cycle, string? accountLastError)
    {
        var fromRun = cycle.SubProfiles
            .Where(IsCycleLevelRun)
            .Select(sp => HumanizeCycleStartError(sp.ErrorMessage ?? sp.ErrorType))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        if (!string.IsNullOrWhiteSpace(fromRun))
        {
            return fromRun!;
        }

        var fromAccount = HumanizeCycleStartError(accountLastError);
        if (!string.IsNullOrWhiteSpace(fromAccount)
            && cycle.Status is MonitoringCycleRunStatuses.Aborted
                or MonitoringCycleRunStatuses.Failed
                or MonitoringCycleRunStatuses.Running)
        {
            return fromAccount;
        }

        return cycle.Status switch
        {
            MonitoringCycleRunStatuses.Running => "браузер не открылся — цикл завис",
            MonitoringCycleRunStatuses.Failed => "ошибка до первого субпрофиля",
            _ => "цикл прерван до первого субпрофиля"
        };
    }

    private static string? HumanizeCycleStartError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (raw.Contains("Session closed", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Page has been closed", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Target.detachedFromTarget", StringComparison.OrdinalIgnoreCase))
        {
            return "браузер закрыл страницу";
        }

        if (raw.Contains("profile is in use", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Profile is in use", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("уже запущен", StringComparison.OrdinalIgnoreCase))
        {
            return "браузерный профиль уже занят";
        }

        return TrimDetail(raw);
    }

    private static string DescribeCycleStop(MonitoringCycleRunSnapshot cycle)
    {
        var ordered = cycle.SubProfiles
            .Where(sp => sp.Outcome != MonitoringSubProfileRunOutcomes.Skipped)
            .OrderBy(sp => sp.StartedAtUtc)
            .ToList();
        if (ordered.Count == 0)
        {
            return cycle.Status == MonitoringCycleRunStatuses.Running
                ? "цикл ещё не начался"
                : "цикл прерван";
        }

        var last = ordered[^1];
        var who = QuoteSubProfile(last.SubProfileName);

        var trailingCaptcha = 0;
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            if (ordered[i].Outcome == MonitoringSubProfileRunOutcomes.Failed
                && string.Equals(ordered[i].ErrorType, "captcha", StringComparison.OrdinalIgnoreCase))
            {
                trailingCaptcha++;
                continue;
            }

            break;
        }

        if (trailingCaptcha >= 2)
        {
            return $"2 капчи подряд, последняя на {who}";
        }

        if (last.Outcome == MonitoringSubProfileRunOutcomes.Started
            && last.CompletedAtUtc is null)
        {
            return cycle.Status == MonitoringCycleRunStatuses.Running
                ? $"сейчас обрабатывается {who}"
                : $"цикл прерван на {who}";
        }

        if (last.Outcome == MonitoringSubProfileRunOutcomes.Failed)
        {
            var kind = last.ErrorType?.Trim() ?? string.Empty;
            if (kind.Equals("captcha", StringComparison.OrdinalIgnoreCase))
            {
                return $"капча на {who}";
            }

            if (kind.Equals("ip-block", StringComparison.OrdinalIgnoreCase))
            {
                return $"блок IP на {who}";
            }

            if (kind.Equals("auth-required", StringComparison.OrdinalIgnoreCase))
            {
                return $"нужен вход ({who})";
            }

            if (kind.Equals("switch-failed", StringComparison.OrdinalIgnoreCase))
            {
                return $"не удалось переключить {who}";
            }

            if (kind.Equals("proxy", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(last.ErrorMessage)
                    ? $"прокси на {who}"
                    : TrimDetail(last.ErrorMessage!);
            }

            if (!string.IsNullOrWhiteSpace(last.ErrorMessage))
            {
                return $"{TrimDetail(last.ErrorMessage!)} ({who})";
            }

            return string.IsNullOrWhiteSpace(kind) ? $"ошибка на {who}" : $"{kind} на {who}";
        }

        return cycle.Status switch
        {
            MonitoringCycleRunStatuses.Running => $"цикл ещё идёт ({who})",
            MonitoringCycleRunStatuses.Failed => "ошибка цикла",
            _ => "цикл прерван"
        };
    }

    private static DateTime CycleStopTimestamp(MonitoringCycleRunSnapshot cycle)
    {
        if (cycle.FinishedAtUtc is DateTime finished)
        {
            return finished;
        }

        var lastActivity = cycle.SubProfiles
            .Select(x => x.CompletedAtUtc ?? x.StartedAtUtc)
            .DefaultIfEmpty(cycle.StartedAtUtc)
            .Max();
        return lastActivity;
    }

    private static string QuoteSubProfile(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "субпрофиль" : name.Trim();
        return $"«{trimmed}»";
    }

    private static string TrimDetail(string detail)
    {
        var trimmed = detail.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80];
    }

    private static int? MatchRunToRowPosition(
        MonitoringSubProfileRunSnapshot run,
        IReadOnlyList<AccountDayRow> rows)
    {
        var id = run.SubProfileId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(id))
        {
            var byId = rows.FirstOrDefault(r =>
                !string.IsNullOrWhiteSpace(r.Id)
                && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId.Position;
            }
        }

        var name = run.SubProfileName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = rows.FirstOrDefault(r =>
                string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName.Position;
            }
        }

        // Last resort: journal position if it falls inside matrix.
        if (run.Position > 0 && run.Position <= rows.Count)
        {
            return run.Position;
        }

        return null;
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

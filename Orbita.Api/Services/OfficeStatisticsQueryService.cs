using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class OfficeStatisticsQueryService(
    OrbitaDbContext db,
    OfficeScopeService officeScope,
    WorkerConnectionRegistry connectionRegistry,
    IOrbitaQueryCache? queryCache = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Test-only: static statistics cache must not leak across InMemory DB fixtures.</summary>
    internal static void ClearCacheForTests() => PanelAggregateCache.Clear();

    public async Task<OfficeStatisticsDto> GetStatisticsAsync(
        OfficeScope scope,
        Guid? officeFilter,
        DateTime? from,
        DateTime? to,
        IReadOnlyList<Guid>? workerIdsFilter = null,
        IReadOnlyList<Guid>? accountIdsFilter = null,
        string? vacancyFilter = null,
        int? timeZoneOffsetMinutes = null,
        CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        // Panel always sends tz; null keeps OS-local for direct API callers.
        var (startLocal, endLocal, utcStart, utcEnd) = LocalCalendarDateRange.Normalize(
            from,
            to,
            timeZoneOffsetMinutes,
            nowUtc);
        var workerFilterSet = NormalizeFilter(workerIdsFilter);
        var accountFilterSet = NormalizeFilter(accountIdsFilter);
        var workerFilterKey = BuildFilterKey(workerFilterSet);
        var accountFilterKey = BuildFilterKey(accountFilterSet);
        var vacancyFilterKey = string.IsNullOrWhiteSpace(vacancyFilter) ? string.Empty : vacancyFilter.Trim();
        var cacheKey = PanelAggregateCache.StatisticsKey(
            scope,
            officeFilter,
            startLocal,
            endLocal,
            workerFilterKey,
            accountFilterKey,
            vacancyFilterKey);

        async Task<OfficeStatisticsDto> ComputeAsync()
        {
        var workersQuery = officeScope
            .ApplyWorkerFilter(db.Workers.AsNoTracking(), scope, officeFilter)
            .Where(x => x.MachineName != LeadFlowImportWorker.MachineName);
        var workers = await workersQuery
            .Select(w => new WorkerProjection(
                w.Id,
                w.DisplayName,
                w.OfficeId,
                w.Office.Name,
                w.LastSeenAtUtc))
            .ToListAsync(ct);

        if (workerFilterSet is not null)
        {
            workers = workers.Where(w => workerFilterSet.Contains(w.Id)).ToList();
        }

        var workerIds = workers.Select(w => w.Id).ToHashSet();
        if (workerIds.Count == 0)
        {
            return Empty(nowUtc);
        }

        var latestSnapshots = await db.WorkerSnapshots
            .AsNoTracking()
            .Where(x => workerIds.Contains(x.WorkerId))
            .GroupBy(x => x.WorkerId)
            .Select(g => g.OrderByDescending(x => x.CapturedAtUtc).First())
            .ToListAsync(ct);

        var statsList = latestSnapshots
            .Select(s => JsonSerializer.Deserialize<DashboardStatsDto>(s.StatsJson, JsonOptions))
            .Where(s => s is not null)
            .Cast<DashboardStatsDto>()
            .ToList();

        var snapshotBalancesByWorker = latestSnapshots
            .ToDictionary(
                s => s.WorkerId,
                s => DeserializeSnapshotBalances(s.BalancesJson));

        var accountRows = await db.WorkerAccounts
            .AsNoTracking()
            .Where(a => workerIds.Contains(a.WorkerId))
            .Select(a => new AccountProjection(
                a.WorkerId,
                a.AccountId,
                a.DisplayName,
                a.Status,
                a.IsEnabledInPanel,
                a.TotalBalance,
                a.SubProfilesJson,
                a.SubProfilesDisabledIdsJson,
                a.LastErrorMessage,
                a.LastMonitoringAt))
            .ToListAsync(ct);

        if (accountFilterSet is not null)
        {
            accountRows = accountRows.Where(a => accountFilterSet.Contains(a.AccountId)).ToList();
        }

        var workerLookup = workers.ToDictionary(w => w.Id);
        var balances = BuildBalances(accountRows, snapshotBalancesByWorker, workerLookup);
        var accountInfrastructure = BuildAccountInfrastructure(accountRows, statsList);

        // Scoped responses (worker / account / vacancy) without time filter — used for send-date joins.
        var scopedResponsesQuery = db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.WorkerId != null && workerIds.Contains(x.WorkerId.Value));
        if (accountFilterSet is not null)
        {
            scopedResponsesQuery = scopedResponsesQuery.Where(x => accountFilterSet.Contains(x.AccountId));
        }

        scopedResponsesQuery = ApplyVacancyFilter(scopedResponsesQuery, vacancyFilterKey);

        // Collected-in-period: totals, duplicates, errors, HR, etc.
        var collectedQuery = scopedResponsesQuery
            .Where(x => x.CollectedAt >= utcStart && x.CollectedAt < utcEnd);

        // Bitrix "sent" metrics use actual send time, not CollectedAt.
        var bitrixSends = await LoadBitrixSendEventsAsync(scopedResponsesQuery, utcStart, utcEnd, ct);

        var dailyTrend = await BuildDailyTrendAsync(
            collectedQuery,
            bitrixSends,
            startLocal,
            endLocal,
            timeZoneOffsetMinutes,
            ct);
        var responses = await BuildResponsesPeriodAsync(collectedQuery, dailyTrend, ct);
        var bitrixDeliveries = await BuildBitrixDeliveryStatsAsync(bitrixSends, scope, officeFilter, ct);
        var crmDeliveries = await BuildCrmDeliveryStatsAsync(
            scopedResponsesQuery,
            utcStart,
            utcEnd,
            scope,
            officeFilter,
            ct);
        var hrInsights = await BuildHrInsightsAsync(collectedQuery, ct);
        var workerInfrastructure = await BuildWorkerInfrastructureAsync(
            workers,
            workerIds,
            utcStart,
            utcEnd,
            accountRows,
            bitrixSends,
            ct);
        var monitoringCycles = await BuildMonitoringCyclesAsync(
            workerIds,
            utcStart,
            utcEnd,
            startLocal,
            endLocal,
            accountRows,
            ct);

        return new OfficeStatisticsDto(
            balances,
            accountInfrastructure,
            workerInfrastructure,
            responses,
            dailyTrend,
            bitrixDeliveries,
            crmDeliveries,
            hrInsights,
            monitoringCycles,
            nowUtc);
        }

        var result = queryCache is null
            ? await PanelAggregateCache.GetOrCreateAsync(cacheKey, PanelAggregateCache.StatisticsTtl, ComputeAsync)
            : await queryCache.GetOrCreateAsync(
                OrbitaCacheDomain.Dashboard,
                scope.ResolveFilter(officeFilter),
                scope.IsGlobalAdmin ? "global-admin" : $"office:{scope.OfficeId?.ToString("D") ?? "-"}",
                new
                {
                    Kind = "statistics",
                    startLocal,
                    endLocal,
                    timeZoneOffsetMinutes,
                    Workers = workerFilterSet?.OrderBy(x => x).ToArray(),
                    Accounts = accountFilterSet?.OrderBy(x => x).ToArray(),
                    vacancyFilterKey
                },
                OrbitaCachePolicy.Realtime,
                _ => ComputeAsync(),
                ct);
        return await RefreshOnlineStatusAsync(result, ct);
    }

    private static HashSet<Guid>? NormalizeFilter(IReadOnlyList<Guid>? ids) =>
        ids is null or { Count: 0 } ? null : ids.ToHashSet();

    private static string BuildFilterKey(HashSet<Guid>? ids) =>
        ids is null ? string.Empty : string.Join(',', ids.OrderBy(x => x));

    private static IQueryable<CandidateResponseEntity> ApplyVacancyFilter(
        IQueryable<CandidateResponseEntity> query,
        string? vacancy)
    {
        foreach (var token in SearchQueryNormalizer.Tokenize(vacancy))
        {
            var pattern = SearchQueryNormalizer.ToILikePattern(token);
            query = query.Where(x =>
                EF.Functions.ILike(x.Vacancy, pattern)
                || EF.Functions.ILike(x.SourceResponseId, pattern)
                || EF.Functions.ILike(x.VacancyUrl, pattern));
        }

        return query;
    }

    private async Task<OfficeStatisticsDto> RefreshOnlineStatusAsync(
        OfficeStatisticsDto result,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var workerIds = result.Workers.Items.Select(w => w.Id).ToList();
        if (workerIds.Count == 0)
        {
            return result with { AggregatedAtUtc = nowUtc };
        }

        var lastSeenRows = await db.Workers
            .AsNoTracking()
            .Where(w => workerIds.Contains(w.Id))
            .Select(w => new { w.Id, w.LastSeenAtUtc })
            .ToListAsync(ct);
        var lastSeenLookup = lastSeenRows.ToDictionary(x => x.Id, x => x.LastSeenAtUtc);

        var refreshedItems = result.Workers.Items
            .Select(w => w with
            {
                IsOnline = WorkerOnlineRules.IsOnline(
                    lastSeenLookup.TryGetValue(w.Id, out var seen) ? seen : null,
                    nowUtc,
                    connectionRegistry.IsConnected(w.Id))
            })
            .ToList();

        return result with
        {
            AggregatedAtUtc = nowUtc,
            Workers = result.Workers with
            {
                Online = refreshedItems.Count(w => w.IsOnline),
                Items = refreshedItems
            }
        };
    }

    private static OfficeStatisticsDto Empty(DateTime aggregatedAtUtc) =>
        new(
            new BalanceStatisticsSection(0, 0, 0, []),
            new AccountInfrastructureSection(0, new DashboardAccountStatusCounts(0, 0, 0, 0), 0, 0),
            new WorkerInfrastructureSection(0, 0, []),
            new ResponsesPeriodSection(0, 0, 0, 0, 0, 0, 0, 0, null),
            [],
            [],
            [],
            new HrInsightsDto([], [], [], [], "н/д", "0%"),
            MonitoringCycleReportBuilder.Build([], DateTime.Today, DateTime.Today),
            aggregatedAtUtc);

    private async Task<MonitoringCycleReportDto> BuildMonitoringCyclesAsync(
        HashSet<Guid> workerIds,
        DateTime utcStart,
        DateTime utcEnd,
        DateTime startLocal,
        DateTime endLocal,
        IReadOnlyList<AccountProjection> accountRows,
        CancellationToken ct)
    {
        var allowedAccountNames = accountRows
            .Select(a => a.DisplayName.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowedAccountNames.Count == 0)
        {
            return MonitoringCycleReportBuilder.Build([], startLocal, endLocal);
        }

        // Prefer the typed monitoring journal.
        var journalCycles = await LoadMonitoringCycleJournalAsync(workerIds, utcStart, utcEnd, ct);
        if (journalCycles.Count > 0)
        {
            var accountCatalog = BuildMonitoringAccountCatalog(accountRows);
            var accountLastErrors = BuildMonitoringAccountLastErrors(accountRows);
            var collected = await LoadMonitoringCollectedResponsesAsync(
                workerIds,
                utcStart,
                utcEnd,
                allowedAccountNames,
                ct);
            return MonitoringCycleReportBuilder.BuildFromJournal(
                journalCycles,
                startLocal,
                endLocal,
                allowedAccountNames,
                collected,
                accountCatalog,
                accountLastErrors);
        }

        // Monitoring activity is recorded only by the typed journal. File logs are
        // diagnostic data and must never be interpreted as statistics.
        return MonitoringCycleReportBuilder.Empty(isDetailed: false);
    }

    private async Task<IReadOnlyList<MonitoringCycleRunSnapshot>> LoadMonitoringCycleJournalAsync(
        HashSet<Guid> workerIds,
        DateTime utcStart,
        DateTime utcEnd,
        CancellationToken ct)
    {
        var cycleRows = await db.MonitoringCycleRuns
            .AsNoTracking()
            .Where(x => workerIds.Contains(x.WorkerId))
            .Where(x => x.StartedAtUtc >= utcStart && x.StartedAtUtc < utcEnd)
            .Select(x => new
            {
                x.Id,
                x.AccountName,
                x.StartedAtUtc,
                x.FinishedAtUtc,
                x.Status
            })
            .ToListAsync(ct);

        if (cycleRows.Count == 0)
        {
            return [];
        }

        var cycleIds = cycleRows.Select(x => x.Id).ToList();
        var subRows = await db.MonitoringSubProfileRuns
            .AsNoTracking()
            .Where(x => cycleIds.Contains(x.CycleRunId))
            .Select(x => new
            {
                x.Id,
                x.CycleRunId,
                x.SubProfileId,
                x.SubProfileName,
                x.Position,
                x.Total,
                x.StartedAtUtc,
                x.CompletedAtUtc,
                x.Outcome,
                x.ErrorType,
                x.ErrorMessage,
                x.PublishedCount,
                x.FoundCount,
                x.CollectedCount,
                x.CaptchaCount,
                x.CaptchaSolvedCount
            })
            .ToListAsync(ct);

        var subsByCycle = subRows
            .GroupBy(x => x.CycleRunId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<MonitoringSubProfileRunSnapshot>)g
                    .Select(s => new MonitoringSubProfileRunSnapshot(
                        s.Id,
                        s.SubProfileId,
                        s.SubProfileName,
                        s.Position,
                        s.Total,
                        s.StartedAtUtc,
                        s.CompletedAtUtc,
                        s.Outcome,
                        s.ErrorType,
                        s.ErrorMessage,
                        s.PublishedCount,
                        s.FoundCount,
                        s.CollectedCount,
                        s.CaptchaCount,
                        s.CaptchaSolvedCount))
                    .OrderBy(s => s.Position)
                    .ThenBy(s => s.StartedAtUtc)
                    .ToList());

        return cycleRows
            .Select(c => new MonitoringCycleRunSnapshot(
                c.Id,
                c.AccountName.Trim(),
                c.StartedAtUtc,
                c.FinishedAtUtc,
                c.Status,
                subsByCycle.GetValueOrDefault(c.Id) ?? []))
            .ToList();
    }

    /// <summary>
    /// Newly collected responses in the monitoring window, used as the source of truth
    /// for «откликов за проход». Republishes do not change CollectedAt.
    /// </summary>
    private async Task<IReadOnlyList<MonitoringCycleSentResponse>> LoadMonitoringCollectedResponsesAsync(
        HashSet<Guid> workerIds,
        DateTime utcStart,
        DateTime utcEnd,
        IReadOnlySet<string> allowedAccountNames,
        CancellationToken ct)
    {
        var padStart = utcStart.AddHours(-12);
        var padEnd = utcEnd.AddHours(12);
        var rows = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.WorkerId != null && workerIds.Contains(x.WorkerId.Value))
            .Where(x => x.CollectedAt >= padStart && x.CollectedAt < padEnd)
            .Select(x => new
            {
                x.AccountName,
                x.AvitoSubProfileId,
                x.AvitoSubProfileName,
                x.CollectedAt
            })
            .ToListAsync(ct);

        return rows
            .Select(x => new MonitoringCycleSentResponse(
                (x.AccountName ?? string.Empty).Trim(),
                (x.AvitoSubProfileName ?? string.Empty).Trim(),
                x.CollectedAt,
                (x.AvitoSubProfileId ?? string.Empty).Trim()))
            .Where(x => allowedAccountNames.Contains(x.AccountName))
            .ToList();
    }

    /// <summary>
    /// Successful Bitrix sends in [utcStart, utcEnd) by <b>send time</b>
    /// (delivery CreatedAtUtc, or ProcessedAt for legacy rows without delivery records).
    /// </summary>
    private async Task<IReadOnlyList<BitrixSendEvent>> LoadBitrixSendEventsAsync(
        IQueryable<CandidateResponseEntity> scopedResponses,
        DateTime utcStart,
        DateTime utcEnd,
        CancellationToken ct)
    {
        var fromDeliveries = await (
            from d in db.ResponseBitrixDeliveries.AsNoTracking()
            where d.Outcome == ResponseBitrixDeliveryOutcomes.Sent
                  && d.CreatedAtUtc >= utcStart
                  && d.CreatedAtUtc < utcEnd
            join r in scopedResponses on d.ResponseId equals r.Id
            select new BitrixSendEvent(
                r.Id,
                r.WorkerId,
                r.AccountName,
                r.AvitoSubProfileName,
                d.CreatedAtUtc,
                d.BitrixInstanceId))
            .ToListAsync(ct);

        var responsesWithAnyDelivery = db.ResponseBitrixDeliveries
            .AsNoTracking()
            .Select(d => d.ResponseId);

        // Legacy: Status=Sent, no delivery rows — period by ProcessedAt (actual send/process time).
        var legacy = await scopedResponses
            .Where(r => r.Status == ResponseStatuses.Sent
                        && r.ProcessedAt != null
                        && r.ProcessedAt >= utcStart
                        && r.ProcessedAt < utcEnd
                        && !responsesWithAnyDelivery.Contains(r.Id))
            .Select(r => new BitrixSendEvent(
                r.Id,
                r.WorkerId,
                r.AccountName,
                r.AvitoSubProfileName,
                r.ProcessedAt!.Value,
                r.BitrixInstanceId ?? Guid.Empty))
            .ToListAsync(ct);

        if (legacy.Count == 0)
        {
            return fromDeliveries;
        }

        var merged = new List<BitrixSendEvent>(fromDeliveries.Count + legacy.Count);
        merged.AddRange(fromDeliveries);
        merged.AddRange(legacy);
        return merged;
    }

    private static BalanceStatisticsSection BuildBalances(
        IReadOnlyList<AccountProjection> accountRows,
        IReadOnlyDictionary<Guid, Dictionary<Guid, WorkerBalanceDto>> snapshotBalancesByWorker,
        IReadOnlyDictionary<Guid, WorkerProjection> workerLookup)
    {
        var items = new List<AccountBalanceStatDto>(accountRows.Count);

        foreach (var row in accountRows)
        {
            if (!workerLookup.TryGetValue(row.WorkerId, out var worker))
            {
                continue;
            }

            decimal advance;
            decimal wallet;
            IReadOnlyList<SubProfileBalanceDto> subProfiles;

            if (snapshotBalancesByWorker.TryGetValue(row.WorkerId, out var workerBalances)
                && workerBalances.TryGetValue(row.AccountId, out var snapshot)
                && BalanceSnapshotHelper.HasMeaningfulBalanceData(snapshot))
            {
                advance = snapshot.TotalBalance;
                wallet = snapshot.TotalWalletBalance;
                subProfiles = snapshot.SubProfiles;
            }
            else
            {
                subProfiles = DeserializeSubProfileBalances(row.SubProfilesJson);
                advance = row.TotalBalance > 0 ? row.TotalBalance : subProfiles.Sum(s => s.Balance ?? 0m);
                wallet = subProfiles.Sum(s => s.WalletBalance ?? 0m);
            }

            var isLowBalance = advance < BalanceDisplayRules.LowBalanceThresholdRub
                || subProfiles.Any(s => s.Balance is decimal b && b < BalanceDisplayRules.LowBalanceThresholdRub);

            items.Add(new AccountBalanceStatDto(
                row.AccountId,
                row.DisplayName,
                row.WorkerId,
                worker.DisplayName,
                worker.OfficeName,
                advance,
                wallet,
                subProfiles,
                isLowBalance));
        }

        var ordered = items
            .OrderBy(a => a.Advance)
            .ThenBy(a => a.AccountName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BalanceStatisticsSection(
            ordered.Sum(a => a.Advance),
            ordered.Sum(a => a.Wallet),
            ordered.Count(a => a.IsLowBalance),
            ordered);
    }

    private static AccountInfrastructureSection BuildAccountInfrastructure(
        IReadOnlyList<AccountProjection> accountRows,
        IReadOnlyList<DashboardStatsDto> statsList)
    {
        var breakdown = AccountDashboardStatusClassifier.Summarize(
            accountRows.Select(a => (a.Status, a.IsEnabledInPanel)));

        return new AccountInfrastructureSection(
            breakdown.Total,
            new DashboardAccountStatusCounts(
                breakdown.Active,
                breakdown.Inactive,
                breakdown.Blocked,
                breakdown.Errors),
            statsList.Sum(s => s.ActiveAdsCount),
            statsList.Sum(s => s.BlockedAdsCount));
    }

    private static async Task<ResponsesPeriodSection> BuildResponsesPeriodAsync(
        IQueryable<CandidateResponseEntity> collectedQuery,
        IReadOnlyList<DailyResponseBucketDto> dailyTrend,
        CancellationToken ct)
    {
        var total = dailyTrend.Sum(x => x.Total);
        // Sent is by send date — can be > 0 even when nothing was collected in the period.
        var sent = dailyTrend.Sum(x => x.Sent);
        if (total == 0 && sent == 0)
        {
            return new ResponsesPeriodSection(0, 0, 0, 0, 0, 0, 0, 0, null);
        }

        var duplicates = dailyTrend.Sum(x => x.Duplicates);
        var errors = dailyTrend.Sum(x => x.Errors);
        var actionRequired = dailyTrend.Sum(x => x.ActionRequired);
        var inProgress = dailyTrend.Sum(x => x.InProgress);
        var unique = Math.Max(0, total - duplicates);
        var uniqueAuthors = total == 0
            ? 0
            : await ResponseSummaryMetrics.CountUniqueAuthorsAsync(collectedQuery, ct);

        double? avgMinutes = null;
        if (total > 0)
        {
            avgMinutes = await collectedQuery
                .Where(x => x.ProcessedAt != null)
                .AverageAsync(x => (double?)(x.ProcessedAt!.Value - x.CollectedAt).TotalMinutes, ct);
        }

        return new ResponsesPeriodSection(
            total,
            unique,
            duplicates,
            sent,
            inProgress,
            actionRequired,
            errors,
            uniqueAuthors,
            avgMinutes > 0 ? avgMinutes : null);
    }

    private async Task<IReadOnlyList<BitrixDeliveryStatDto>> BuildBitrixDeliveryStatsAsync(
        IReadOnlyList<BitrixSendEvent> bitrixSends,
        OfficeScope scope,
        Guid? officeFilter,
        CancellationToken ct)
    {
        // Count successful deliveries by instance at send time (not collection time).
        var merged = bitrixSends
            .Where(x => x.BitrixInstanceId != Guid.Empty)
            .GroupBy(x => x.BitrixInstanceId)
            .ToDictionary(g => g.Key, g => g.Count());

        if (merged.Count == 0)
        {
            return [];
        }

        var instancesQuery = db.BitrixInstances.AsNoTracking();
        var effectiveOfficeId = scope.ResolveFilter(officeFilter);
        if (effectiveOfficeId is Guid officeId)
        {
            instancesQuery = instancesQuery.Where(x => x.OfficeId == officeId);
        }
        else if (!scope.IsGlobalAdmin)
        {
            return [];
        }

        var instances = await instancesQuery
            .Where(x => merged.Keys.Contains(x.Id))
            .Select(x => new { x.Id, x.Name, x.Signature })
            .ToListAsync(ct);

        return instances
            .Select(x => new BitrixDeliveryStatDto(
                x.Id,
                ResponseBitrixDeliveryService.FormatBitrixLabel(x.Name, x.Signature),
                merged[x.Id]))
            .OrderByDescending(x => x.SentCount)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<CrmDeliveryStatDto>> BuildCrmDeliveryStatsAsync(
        IQueryable<CandidateResponseEntity> scopedResponses,
        DateTime utcStart,
        DateTime utcEnd,
        OfficeScope scope,
        Guid? officeFilter,
        CancellationToken ct)
    {
        // CRM sends by actual delivery CreatedAtUtc.
        var deliveriesQuery =
            from d in db.ResponseCrmDeliveries.AsNoTracking()
            where d.Outcome == ResponseCrmDeliveryOutcomes.Sent
                  && d.CreatedAtUtc >= utcStart
                  && d.CreatedAtUtc < utcEnd
            join r in scopedResponses on d.ResponseId equals r.Id
            select d;

        var effectiveOfficeId = scope.ResolveFilter(officeFilter);
        if (effectiveOfficeId is Guid officeId)
        {
            deliveriesQuery = deliveriesQuery.Where(d => d.OfficeId == officeId);
        }
        else if (!scope.IsGlobalAdmin)
        {
            return [];
        }

        var deliveryRows = await deliveriesQuery
            .GroupBy(d => new { d.OfficeId, OfficeName = d.Office.Name })
            .Select(g => new { g.Key.OfficeId, g.Key.OfficeName, Count = g.Count() })
            .ToListAsync(ct);

        // Delivery journal was added after CRM cards already existed. Count those cards by
        // creation time only when no journal entry exists for the same response and office.
        var legacyCardsQuery =
            from card in db.CrmCandidateCards.AsNoTracking()
            where card.CreatedAtUtc >= utcStart
                  && card.CreatedAtUtc < utcEnd
                  && !db.ResponseCrmDeliveries.Any(d =>
                      d.ResponseId == card.ResponseId
                      && d.OfficeId == card.OfficeId)
            join response in scopedResponses on card.ResponseId equals response.Id
            join office in db.Offices.AsNoTracking() on card.OfficeId equals office.Id
            select new { card.OfficeId, OfficeName = office.Name };

        if (effectiveOfficeId is Guid legacyOfficeId)
        {
            legacyCardsQuery = legacyCardsQuery.Where(card => card.OfficeId == legacyOfficeId);
        }

        var legacyRows = await legacyCardsQuery
            .GroupBy(card => new { card.OfficeId, card.OfficeName })
            .Select(g => new { g.Key.OfficeId, g.Key.OfficeName, Count = g.Count() })
            .ToListAsync(ct);

        return deliveryRows
            .Concat(legacyRows)
            .GroupBy(x => new { x.OfficeId, x.OfficeName })
            .Select(g => new CrmDeliveryStatDto(g.Key.OfficeId, g.Key.OfficeName, g.Sum(x => x.Count)))
            .OrderByDescending(x => x.SentCount)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<DailyResponseBucketDto>> BuildDailyTrendAsync(
        IQueryable<CandidateResponseEntity> collectedQuery,
        IReadOnlyList<BitrixSendEvent> bitrixSends,
        DateTime startLocal,
        DateTime endLocal,
        int? timeZoneOffsetMinutes,
        CancellationToken ct)
    {
        var rows = await collectedQuery
            .GroupBy(x => new { Date = x.CollectedAt.Date, x.CollectedAt.Hour, x.Status })
            .Select(g => new HourlyStatusCount(g.Key.Date, g.Key.Hour, g.Key.Status, g.Count()))
            .ToListAsync(ct);

        var byDay = new Dictionary<DateTime, DailyCounters>();
        foreach (var row in rows)
        {
            var utcHour = DateTime.SpecifyKind(row.Date.AddHours(row.Hour), DateTimeKind.Utc);
            var localDate = LocalCalendarDateRange.ToLocalDateFromStoredUtc(utcHour, timeZoneOffsetMinutes);
            if (!byDay.TryGetValue(localDate, out var bucket))
            {
                bucket = new DailyCounters();
                byDay[localDate] = bucket;
            }

            bucket.Total += row.Count;
            // Sent is filled below from actual send timestamps — not from CollectedAt + Status.
            switch (row.Status)
            {
                case ResponseStatuses.Duplicate:
                    bucket.Duplicates += row.Count;
                    break;
                case ResponseStatuses.Error:
                    bucket.Errors += row.Count;
                    break;
                case ResponseStatuses.InProgress:
                    bucket.InProgress += row.Count;
                    break;
                case ResponseStatuses.ActionRequired:
                    bucket.ActionRequired += row.Count;
                    break;
            }
        }

        // One count per response on the local day of its first Bitrix send in the period.
        foreach (var sendGroup in bitrixSends.GroupBy(x => x.ResponseId))
        {
            var firstSend = sendGroup.Min(x => x.SentAtUtc);
            var localDate = LocalCalendarDateRange.ToLocalDateFromStoredUtc(firstSend, timeZoneOffsetMinutes);
            if (localDate < startLocal.Date || localDate > endLocal.Date)
            {
                continue;
            }

            if (!byDay.TryGetValue(localDate, out var bucket))
            {
                bucket = new DailyCounters();
                byDay[localDate] = bucket;
            }

            bucket.Sent += 1;
        }

        var list = new List<DailyResponseBucketDto>((endLocal - startLocal).Days + 1);
        for (var day = startLocal; day <= endLocal; day = day.AddDays(1))
        {
            if (!byDay.TryGetValue(day, out var counters))
            {
                list.Add(new DailyResponseBucketDto(day, 0, 0, 0, 0, 0, 0));
                continue;
            }

            list.Add(new DailyResponseBucketDto(
                day,
                counters.Total,
                counters.Sent,
                counters.InProgress,
                counters.ActionRequired,
                counters.Duplicates,
                counters.Errors));
        }

        return list;
    }

    private static async Task<HrInsightsDto> BuildHrInsightsAsync(
        IQueryable<CandidateResponseEntity> query,
        CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        if (total == 0)
        {
            return new HrInsightsDto([], [], [], [], "н/д", "0%");
        }

        var withMessenger = await query.CountAsync(
            x => x.MessengerUrl != null && x.MessengerUrl != string.Empty,
            ct);
        var avgAge = await query
            .Where(x => x.Age.HasValue)
            .Select(x => (double?)x.Age)
            .AverageAsync(ct);

        var cityRows = await query
            .GroupBy(x => new { x.City, x.Status })
            .Select(g => new GroupedStatusRow(g.Key.City, g.Key.Status, g.Count()))
            .ToListAsync(ct);
        var vacancyRows = await query
            .GroupBy(x => new { x.Vacancy, x.Status })
            .Select(g => new GroupedStatusRow(g.Key.Vacancy, g.Key.Status, g.Count()))
            .ToListAsync(ct);
        var accountRows = await query
            .GroupBy(x => new { x.AccountName, x.Status })
            .Select(g => new GroupedStatusRow(g.Key.AccountName, g.Key.Status, g.Count()))
            .ToListAsync(ct);
        var ageRows = await query
            .GroupBy(x => new { x.Age, x.Status })
            .Select(g => new GroupedAgeStatusRow(g.Key.Age, g.Key.Status, g.Count()))
            .ToListAsync(ct);

        var topCities = BuildTopMetrics(cityRows, "Город не указан", total, 8);
        var topVacancies = BuildTopMetrics(vacancyRows, "Вакансия не указана", total, 8);
        var topAccounts = BuildTopMetrics(accountRows, "Аккаунт не указан", total, 8);
        var ageBuckets = BuildAgeBuckets(ageRows);

        var avgAgeText = avgAge is null ? "н/д" : $"{Math.Round(avgAge.Value, 1):0.#} лет";
        return new HrInsightsDto(
            topCities,
            topVacancies,
            topAccounts,
            ageBuckets,
            avgAgeText,
            Percent(withMessenger, total));
    }

    private async Task<WorkerInfrastructureSection> BuildWorkerInfrastructureAsync(
        IReadOnlyList<WorkerProjection> workers,
        HashSet<Guid> workerIds,
        DateTime utcStart,
        DateTime utcEnd,
        IReadOnlyList<AccountProjection> accountRows,
        IReadOnlyList<BitrixSendEvent> bitrixSends,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var online = workers.Count(w =>
            WorkerOnlineRules.IsOnline(w.LastSeenAtUtc, nowUtc, connectionRegistry.IsConnected(w.Id)));

        var accountCounts = accountRows
            .GroupBy(a => a.WorkerId)
            .ToDictionary(
                g => g.Key,
                g => (
                    Total: g.Count(),
                    Active: g.Count(a => AccountDashboardStatusClassifier.IsActiveInPanel(
                        a.Status,
                        a.IsEnabledInPanel))));

        // Collected-period totals/duplicates/errors still by CollectedAt.
        var responseStats = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.WorkerId != null && workerIds.Contains(x.WorkerId.Value) && x.CollectedAt >= utcStart && x.CollectedAt < utcEnd)
            .GroupBy(x => x.WorkerId)
            .Select(g => new
            {
                WorkerId = g.Key,
                Total = g.Count(),
                Duplicates = g.Count(x => x.Status == ResponseStatuses.Duplicate),
                Errors = g.Count(x => x.Status == ResponseStatuses.Error || x.Status == ResponseStatuses.ActionRequired)
            })
            .ToListAsync(ct);

        var responseLookup = responseStats
            .Where(x => x.WorkerId is not null)
            .ToDictionary(x => x.WorkerId!.Value);

        // PeriodSent: distinct responses with Bitrix send in the period, by worker.
        var sentByWorker = bitrixSends
            .Where(x => x.WorkerId is not null)
            .GroupBy(x => x.WorkerId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.ResponseId).Distinct().Count());

        var items = workers
            .Select(w =>
            {
                responseLookup.TryGetValue(w.Id, out var stats);
                accountCounts.TryGetValue(w.Id, out var accounts);
                sentByWorker.TryGetValue(w.Id, out var periodSent);
                return new WorkerStatisticsRowDto(
                    w.Id,
                    w.DisplayName,
                    w.OfficeName,
                    WorkerOnlineRules.IsOnline(w.LastSeenAtUtc, nowUtc, connectionRegistry.IsConnected(w.Id)),
                    stats?.Total ?? 0,
                    periodSent,
                    stats?.Duplicates ?? 0,
                    stats?.Errors ?? 0,
                    accounts.Active,
                    accounts.Total);
            })
            .OrderByDescending(w => w.PeriodResponses)
            .ThenBy(w => w.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorkerInfrastructureSection(workers.Count, online, items);
    }

    private static IReadOnlyList<HrMetricDto> BuildTopMetrics(
        IEnumerable<GroupedStatusRow> groupedRows,
        string fallback,
        int total,
        int take) =>
        groupedRows
            .GroupBy(x => Normalize(x.Key, fallback))
            .Select(g =>
            {
                var sent = g.Where(x => x.Status == ResponseStatuses.Sent).Sum(x => x.Count);
                var count = g.Sum(x => x.Count);
                return new HrMetricDto(
                    g.Key,
                    count,
                    sent,
                    Percent(sent, count),
                    Percent(count, total));
            })
            .OrderByDescending(x => x.Total)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();

    private static IReadOnlyList<AgeBucketDto> BuildAgeBuckets(IEnumerable<GroupedAgeStatusRow> ageRows) =>
        ageRows
            .GroupBy(x => GetAgeBucket(x.Age))
            .Select(g =>
            {
                var count = g.Sum(x => x.Count);
                var sent = g.Where(x => x.Status == ResponseStatuses.Sent).Sum(x => x.Count);
                return new AgeBucketDto(
                    g.Key,
                    count,
                    sent,
                    Percent(sent, count));
            })
            .OrderByDescending(x => x.Total)
            .ToList();

    private static string GetAgeBucket(int? age) => age switch
    {
        null => "Возраст не указан",
        < 18 => "< 18",
        <= 24 => "18-24",
        <= 34 => "25-34",
        <= 44 => "35-44",
        _ => "45+"
    };

    private static string Percent(int part, int whole) =>
        whole <= 0 ? "0%" : $"{Math.Round(part * 100d / whole, 1):0.#}%";

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static Dictionary<Guid, WorkerBalanceDto> DeserializeSnapshotBalances(string? json) =>
        (JsonSerializer.Deserialize<List<WorkerBalanceDto>>(json ?? "[]", JsonOptions) ?? [])
            .GroupBy(b => b.AccountId)
            .ToDictionary(g => g.Key, g => g.Last());

    private static IReadOnlyList<SubProfileBalanceDto> DeserializeSubProfileBalances(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return [];
        }

        try
        {
            var profiles = JsonSerializer.Deserialize<List<WorkerSubProfileDto>>(json, SubProfileJsonOptions.Deserialize) ?? [];
            return profiles
                .Select(p => new SubProfileBalanceDto(
                    p.Name,
                    p.Balance,
                    p.WalletBalance,
                    p.AdvanceDurationText,
                    p.Id))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record WorkerProjection(
        Guid Id,
        string DisplayName,
        Guid OfficeId,
        string OfficeName,
        DateTime? LastSeenAtUtc);

    private static IReadOnlyDictionary<string, IReadOnlyList<MonitoringAccountSubProfileCatalogEntry>>
        BuildMonitoringAccountCatalog(IReadOnlyList<AccountProjection> accountRows)
    {
        var map = new Dictionary<string, IReadOnlyList<MonitoringAccountSubProfileCatalogEntry>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var account in accountRows)
        {
            var name = account.DisplayName.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var profiles = SubProfileDeserializer.Deserialize(
                account.SubProfilesJson,
                account.SubProfilesDisabledIdsJson);
            if (profiles is null || profiles.Count == 0)
            {
                continue;
            }

            var enabled = profiles
                .Where(p => p.IsEnabledInPanel)
                .Select((p, index) => new MonitoringAccountSubProfileCatalogEntry(
                    index + 1,
                    p.Id?.Trim() ?? string.Empty,
                    string.IsNullOrWhiteSpace(p.Name) ? (p.Id ?? "—") : p.Name.Trim()))
                .ToList();

            if (enabled.Count == 0)
            {
                continue;
            }

            // If several workers share display name, keep the largest enabled set.
            if (!map.TryGetValue(name, out var existing) || enabled.Count > existing.Count)
            {
                map[name] = enabled;
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string?> BuildMonitoringAccountLastErrors(
        IReadOnlyList<AccountProjection> accountRows)
    {
        var map = new Dictionary<string, (DateTime? At, string? Error)>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accountRows)
        {
            var name = account.DisplayName.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(account.LastErrorMessage))
            {
                continue;
            }

            if (!map.TryGetValue(name, out var existing)
                || (account.LastMonitoringAt is DateTime at && (existing.At is null || at > existing.At)))
            {
                map[name] = (account.LastMonitoringAt, account.LastErrorMessage);
            }
        }

        return map.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Error,
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed record AccountProjection(
        Guid WorkerId,
        Guid AccountId,
        string DisplayName,
        string Status,
        bool IsEnabledInPanel,
        decimal TotalBalance,
        string SubProfilesJson,
        string SubProfilesDisabledIdsJson,
        string? LastErrorMessage = null,
        DateTime? LastMonitoringAt = null);

    private sealed class DailyCounters
    {
        public int Total { get; set; }
        public int Sent { get; set; }
        public int InProgress { get; set; }
        public int ActionRequired { get; set; }
        public int Duplicates { get; set; }
        public int Errors { get; set; }
    }

    private sealed record HourlyStatusCount(DateTime Date, int Hour, string Status, int Count);

    private sealed record GroupedStatusRow(string? Key, string Status, int Count);
    private sealed record GroupedAgeStatusRow(int? Age, string Status, int Count);

    private sealed record BitrixSendEvent(
        Guid ResponseId,
        Guid? WorkerId,
        string AccountName,
        string SubProfileName,
        DateTime SentAtUtc,
        Guid BitrixInstanceId);
}

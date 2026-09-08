using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

using static Orbita.Api.Helpers.SubProfilesDisabledIdsHelper;

namespace Orbita.Api.Services;

public sealed class DashboardQueryService(
    OrbitaDbContext db,
    WorkerReleaseService releases,
    OfficeScopeService officeScope,
    WorkerConnectionRegistry connectionRegistry,
    LocalChromeLoginSessionService? localChromeLoginSessions = null,
    IOrbitaQueryCache? queryCache = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Test-only: static summary cache must not leak across InMemory DB fixtures.</summary>
    internal static void ClearCacheForTests() => PanelAggregateCache.Clear();

    public async Task<GlobalDashboardSummary> GetGlobalSummaryAsync(
        OfficeScope scope,
        Guid? officeFilter = null,
        int? timeZoneOffsetMinutes = null,
        DateTime? fromLocal = null,
        DateTime? toLocal = null,
        CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var (startLocal, endLocal, _, _) = LocalCalendarDateRange.Normalize(
            fromLocal,
            toLocal,
            timeZoneOffsetMinutes,
            nowUtc);
        var summary = queryCache is null
            ? await PanelAggregateCache.GetOrCreateAsync(
                PanelAggregateCache.SummaryKey(scope, officeFilter, timeZoneOffsetMinutes, startLocal, endLocal),
                PanelAggregateCache.DashboardTtl,
                () => ComputeGlobalSummaryCoreAsync(scope, officeFilter, timeZoneOffsetMinutes, startLocal, endLocal, ct))
            : await queryCache.GetOrCreateAsync(
                OrbitaCacheDomain.Dashboard,
                scope.ResolveFilter(officeFilter),
                ScopeAudience(scope),
                new { Kind = "summary", timeZoneOffsetMinutes, startLocal, endLocal },
                OrbitaCachePolicy.Realtime,
                token => ComputeGlobalSummaryCoreAsync(scope, officeFilter, timeZoneOffsetMinutes, startLocal, endLocal, token),
                ct);
        return await RefreshOnlineWorkersAsync(summary, scope, officeFilter, ct);
    }

    public async Task<NavBadgesDto> GetNavBadgesAsync(
        OfficeScope scope,
        Guid? officeFilter = null,
        int? timeZoneOffsetMinutes = null,
        CancellationToken ct = default)
    {
        return queryCache is null
            ? await PanelAggregateCache.GetOrCreateAsync(
                PanelAggregateCache.NavBadgesKey(scope, officeFilter, timeZoneOffsetMinutes),
                PanelAggregateCache.NavBadgesTtl,
                () => ComputeNavBadgesCoreAsync(scope, officeFilter, timeZoneOffsetMinutes, ct))
            : await queryCache.GetOrCreateAsync(
                OrbitaCacheDomain.Dashboard,
                scope.ResolveFilter(officeFilter),
                ScopeAudience(scope),
                new { Kind = "nav-badges", timeZoneOffsetMinutes },
                OrbitaCachePolicy.Realtime,
                token => ComputeNavBadgesCoreAsync(scope, officeFilter, timeZoneOffsetMinutes, token),
                ct);
    }

    private static string ScopeAudience(OfficeScope scope) =>
        scope.IsGlobalAdmin ? "global-admin" : $"office:{scope.OfficeId?.ToString("D") ?? "-"}";

    private async Task<GlobalDashboardSummary> ComputeGlobalSummaryCoreAsync(
        OfficeScope scope,
        Guid? officeFilter,
        int? timeZoneOffsetMinutes,
        DateTime startLocal,
        DateTime endLocal,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;

        var todayLocal = LocalCalendarDateRange.GetLocalCalendarDate(nowUtc, timeZoneOffsetMinutes);
        var todayStart = LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(todayLocal, timeZoneOffsetMinutes)
            .UtcStartInclusive;
        var (_, _, periodUtcStart, periodUtcEnd) = LocalCalendarDateRange.Normalize(
            startLocal,
            endLocal,
            timeZoneOffsetMinutes,
            nowUtc);
        var workersQuery = officeScope
            .ApplyWorkerFilter(db.Workers.AsNoTracking(), scope, officeFilter)
            .Where(x => x.MachineName != LeadFlowImportWorker.MachineName);
        var workers = await workersQuery.ToListAsync(ct);
        var workerIds = workers.Select(w => w.Id).ToHashSet();
        var onlineWorkers = workers.Count(w =>
            WorkerOnlineRules.IsOnline(w.LastSeenAtUtc, nowUtc, connectionRegistry.IsConnected(w.Id)));

        // Compute response counts from source of truth (CandidateResponses) for accuracy
        // instead of relying on (often zero) pushed snapshots.
        var responseStats = await ComputeTodayResponseStatsAsync(workerIds, todayStart, ct);

        // "Sent" is counted by actual send time (CRM + Bitrix deliveries), not the response
        // collection date — a response collected yesterday but sent today counts for today.
        var sendTimestamps = await LoadSendTimestampsAsync(workerIds, periodUtcStart, periodUtcEnd, ct);

        // Keep snapshot data only for account-level details (ads counts, balances) and activity charts
        var latestSnapshots = await WorkerSnapshotQuery.LoadLatestAsync(db, workerIds, ct);

        var statsList = latestSnapshots
            .Select(s => JsonSerializer.Deserialize<DashboardStatsDto>(s.StatsJson, JsonOptions))
            .Where(s => s is not null)
            .Cast<DashboardStatsDto>()
            .ToList();

        var balances = latestSnapshots
            .Select(s => JsonSerializer.Deserialize<List<WorkerBalanceDto>>(s.BalancesJson, JsonOptions) ?? [])
            .SelectMany(x => x)
            .ToList();

        // Prefer real response counts; fall back to snapshot sums only for fields not derivable from responses (ads etc.)
        int totalToday = responseStats.TotalToday;
        int sentToCrm = sendTimestamps.Count(t => t >= todayStart);
        int duplicates = responseStats.Duplicates;
        int inProgress = responseStats.InProgress;
        int actionRequired = responseStats.ActionRequired;

        var workerEventErrors = await ComputeWorkerEventErrorStatsAsync(workerIds, todayStart, periodUtcStart, ct);
        int errors = workerEventErrors.TodayCount;

        int connectedAccounts = statsList.Sum(s => s.ConnectedAccounts);
        int requiresAuth = statsList.Sum(s => s.RequiresAuthorization);
        int needAttention = statsList.Sum(s => s.AccountsNeedAttentionCount);
        int activeAds = statsList.Sum(s => s.ActiveAdsCount);
        int blockedAds = statsList.Sum(s => s.BlockedAdsCount);

        var accountStatusCounts = new DashboardAccountStatusCounts(0, 0, 0, 0);

        // Prefer WorkerAccounts for account status breakdown (matches Accounts page classification).
        if (workerIds.Count > 0)
        {
            var accountRows = await db.WorkerAccounts
                .AsNoTracking()
                .Where(a => workerIds.Contains(a.WorkerId))
                .Select(a => new { a.Status, a.IsEnabledInPanel })
                .ToListAsync(ct);

            if (accountRows.Count > 0)
            {
                var breakdown = AccountDashboardStatusClassifier.Summarize(
                    accountRows.Select(a => (a.Status, a.IsEnabledInPanel)));
                connectedAccounts = Math.Max(connectedAccounts, breakdown.Total);
                needAttention = Math.Max(needAttention, breakdown.Errors + breakdown.Blocked);
                accountStatusCounts = new DashboardAccountStatusCounts(
                    breakdown.Active,
                    breakdown.Inactive,
                    breakdown.Blocked,
                    breakdown.Errors);
            }
        }

        var result = new GlobalDashboardSummary(
            TotalWorkers: workers.Count,
            OnlineWorkers: onlineWorkers,
            TotalToday: totalToday,
            SentToCrm: sentToCrm,
            InProgress: inProgress,
            Duplicates: duplicates,
            Errors: errors,
            ActionRequired: actionRequired,
            ConnectedAccounts: connectedAccounts,
            RequiresAuthorization: requiresAuth,
            AccountsNeedAttentionCount: needAttention,
            AccountStatusCounts: accountStatusCounts,
            ActiveAdsCount: activeAds,
            BlockedAdsCount: blockedAds,
            TotalBalance: balances.Sum(b => b.TotalBalance),
            HourlyActivity: await ComputeHourlyActivityFromDbAsync(
                workerIds,
                todayStart,
                sendTimestamps,
                workerEventErrors.Hourly,
                ct),
            WeeklyByDayActivity: WorkerEventErrorStatsHelper.MergeDailyErrors(
                await ComputeDailyActivityFromDbAsync(
                    workerIds,
                    startLocal,
                    endLocal,
                    sendTimestamps,
                    timeZoneOffsetMinutes,
                    ct),
                workerEventErrors.Daily),
            AggregatedAtUtc: nowUtc);

        return result;
    }

    private async Task<NavBadgesDto> ComputeNavBadgesCoreAsync(
        OfficeScope scope,
        Guid? officeFilter,
        int? timeZoneOffsetMinutes,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var todayLocal = LocalCalendarDateRange.GetLocalCalendarDate(nowUtc, timeZoneOffsetMinutes);
        var todayStart = LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(todayLocal, timeZoneOffsetMinutes)
            .UtcStartInclusive;
        var workerIds = await officeScope
            .ApplyWorkerFilter(db.Workers.AsNoTracking(), scope, officeFilter)
            .Where(x => x.MachineName != LeadFlowImportWorker.MachineName)
            .Select(x => x.Id)
            .ToListAsync(ct);
        var workerIdSet = workerIds.ToHashSet();
        if (workerIdSet.Count == 0)
        {
            return new NavBadgesDto(0, 0, 0, nowUtc);
        }

        var responseStats = await ComputeTodayResponseStatsAsync(workerIdSet, todayStart, ct);
        var errorsToday = await CountTodayWorkerEventErrorsAsync(workerIdSet, todayStart, ct);
        return new NavBadgesDto(
            errorsToday,
            Math.Max(0, responseStats.TotalToday - responseStats.Duplicates),
            responseStats.ActionRequired,
            nowUtc);
    }

    private async Task<GlobalDashboardSummary> RefreshOnlineWorkersAsync(
        GlobalDashboardSummary summary,
        OfficeScope scope,
        Guid? officeFilter,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var workers = await officeScope
            .ApplyWorkerFilter(db.Workers.AsNoTracking(), scope, officeFilter)
            .Where(x => x.MachineName != LeadFlowImportWorker.MachineName)
            .Select(w => new { w.Id, w.LastSeenAtUtc })
            .ToListAsync(ct);
        var onlineWorkers = workers.Count(w =>
            WorkerOnlineRules.IsOnline(w.LastSeenAtUtc, nowUtc, connectionRegistry.IsConnected(w.Id)));
        return summary with
        {
            TotalWorkers = workers.Count,
            OnlineWorkers = onlineWorkers,
            AggregatedAtUtc = nowUtc
        };
    }

    public Task<IReadOnlyList<WorkerListItem>> GetWorkersAsync(
        OfficeScope scope,
        Guid? officeFilter = null,
        CancellationToken ct = default)
    {
        Task<IReadOnlyList<WorkerListItem>> Load(CancellationToken token) =>
            GetWorkersUncachedAsync(scope, officeFilter, token);
        return queryCache is null
            ? Load(ct)
            : queryCache.GetOrCreateAsync(
                OrbitaCacheDomain.Dashboard,
                scope.ResolveFilter(officeFilter),
                ScopeAudience(scope),
                new { Kind = "workers" },
                OrbitaCachePolicy.Realtime,
                Load,
                ct);
    }

    private async Task<IReadOnlyList<WorkerListItem>> GetWorkersUncachedAsync(
        OfficeScope scope,
        Guid? officeFilter = null,
        CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var todayStart = nowUtc.Date;
        var workers = await officeScope
            .ApplyWorkerFilter(db.Workers.AsNoTracking(), scope, officeFilter)
            .Where(x => x.MachineName != LeadFlowImportWorker.MachineName)
            .OrderBy(x => x.DisplayName)
            .Select(w => new
            {
                w.Id,
                w.DisplayName,
                w.MachineName,
                w.IpAddress,
                w.AppVersion,
                w.MonitoringStatus,
                w.MonitoringStatusMessage,
                w.IsMonitoringActive,
                w.LastSeenAtUtc,
                w.IsEnabled,
                w.IsMonitoringPaused,
                w.OfficeId,
                OfficeName = w.Office.Name,
                w.ActivityPhase,
                w.ActivityMessage,
                w.ActivityAccountId,
                w.ActivityAccountName,
                w.ActivitySubProfileId,
                w.ActivitySubProfileName,
                w.ActivityUpdatedAtUtc,
                w.ActivityNextCycleAtUtc,
                w.ActivityActiveAccountsJson
            })
            .ToListAsync(ct);

        var workerIds = workers.Select(w => w.Id).ToList();
        var operationalStats = await ComputeWorkerOperationalStatsAsync(workerIds, todayStart, ct);

        var latestStats = (await WorkerSnapshotQuery.LoadLatestAsync(db, workerIds, ct))
            .ToDictionary(x => x.WorkerId, x => x.StatsJson);

        var latestRelease = await releases.GetLatestAsync(ct);
        var latestReleaseVersion = latestRelease?.Version;

        return workers.Select(w =>
        {
            DashboardStatsDto? stats = null;
            if (latestStats.TryGetValue(w.Id, out var json))
            {
                stats = JsonSerializer.Deserialize<DashboardStatsDto>(json, JsonOptions);
            }

            operationalStats.TryGetValue(w.Id, out var op);
            op ??= WorkerOperationalStats.Empty;
            var updateAvailable = AppVersionHelper.IsNewer(latestReleaseVersion, w.AppVersion);
            return new WorkerListItem(
                w.Id,
                w.DisplayName,
                w.MachineName,
                w.AppVersion,
                w.MonitoringStatus,
                w.MonitoringStatusMessage,
                w.IsMonitoringActive,
                WorkerOnlineRules.IsOnline(w.LastSeenAtUtc, nowUtc, connectionRegistry.IsConnected(w.Id)),
                w.LastSeenAtUtc,
                op.TotalAccounts,
                op.TodayResponses,
                op.TodayDuplicates,
                op.TodayEventErrors,
                updateAvailable,
                latestReleaseVersion,
                w.OfficeId,
                w.OfficeName,
                w.IsEnabled,
                op.ActiveAccounts,
                op.LowBalanceAccountCount,
                WorkerActivityMapper.ToDto(
                    w.ActivityPhase,
                    w.ActivityMessage,
                    w.ActivityAccountId,
                    w.ActivityAccountName,
                    w.ActivitySubProfileId,
                    w.ActivitySubProfileName,
                    w.ActivityNextCycleAtUtc,
                    w.ActivityUpdatedAtUtc,
                    WorkerActivityMapper.DeserializeActiveAccounts(w.ActivityActiveAccountsJson)),
                WorkerActivityMapper.DeserializeActiveAccounts(w.ActivityActiveAccountsJson),
                w.IsMonitoringPaused,
                w.IpAddress ?? string.Empty);
        }).ToList();
    }

    public Task<WorkersPageDto> GetWorkersPageAsync(
        OfficeScope scope,
        Guid? officeFilter = null,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? dir = null,
        CancellationToken ct = default,
        string? workerFilter = null,
        string? workerSearch = null)
    {
        Task<WorkersPageDto> Load(CancellationToken token) => GetWorkersPageUncachedAsync(
            scope, officeFilter, page, pageSize, sort, dir, token, workerFilter, workerSearch);
        return queryCache is null
            ? Load(ct)
            : queryCache.GetOrCreateAsync(
                OrbitaCacheDomain.Dashboard,
                scope.ResolveFilter(officeFilter),
                ScopeAudience(scope),
                new { Kind = "workers-page", page, pageSize, sort, dir, workerFilter, workerSearch },
                OrbitaCachePolicy.Realtime,
                Load,
                ct);
    }

    private async Task<WorkersPageDto> GetWorkersPageUncachedAsync(
        OfficeScope scope,
        Guid? officeFilter = null,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? dir = null,
        CancellationToken ct = default,
        string? workerFilter = null,
        string? workerSearch = null)
    {
        var nowUtc = DateTime.UtcNow;
        var todayStart = nowUtc.Date;
        var normalizedPageSize = WorkerListPaging.NormalizePageSize(pageSize);
        var (sortColumn, sortDescending) = WorkerListPaging.NormalizeSort(sort, dir);

        var filtered = officeScope
            .ApplyWorkerFilter(db.Workers.AsNoTracking(), scope, officeFilter)
            .Where(x => x.MachineName != LeadFlowImportWorker.MachineName);

        foreach (var token in SearchQueryNormalizer.Tokenize(workerSearch))
        {
            var normalizedToken = token.ToLowerInvariant();
            filtered = filtered.Where(x =>
                x.DisplayName.ToLower().Contains(normalizedToken)
                || x.MachineName.ToLower().Contains(normalizedToken)
                || (x.IpAddress != null && x.IpAddress.ToLower().Contains(normalizedToken)));
        }

        var pauseGroups = await filtered
            .GroupBy(x => x.IsMonitoringPaused)
            .Select(g => new { Paused = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var totalCount = pauseGroups.Sum(x => x.Count);
        var pausedCount = pauseGroups.FirstOrDefault(x => x.Paused)?.Count ?? 0;
        var enabledCount = totalCount - pausedCount;
        if (totalCount == 0)
        {
            return new WorkersPageDto(
                [],
                0,
                WorkerListPaging.NormalizePage(page, normalizedPageSize, 0),
                normalizedPageSize,
                sortColumn,
                sortDescending ? "desc" : "asc",
                0,
                0);
        }

        // The latest telemetry snapshot is the source of truth for a balance. WorkerAccounts
        // can lag behind it when the worker sends a balance for an account that is not in the
        // current account catalogue. Load the ordered candidate set first, then apply the
        // low-balance group after computing those snapshots below.
        var ordered = ApplyWorkerListSort(filtered, sortColumn, sortDescending, todayStart);
        var workers = await ordered
            .Select(w => new
            {
                w.Id,
                w.DisplayName,
                w.MachineName,
                w.IpAddress,
                w.AppVersion,
                w.MonitoringStatus,
                w.MonitoringStatusMessage,
                w.IsMonitoringActive,
                w.LastSeenAtUtc,
                w.IsEnabled,
                w.IsMonitoringPaused,
                w.OfficeId,
                OfficeName = w.Office.Name,
                w.ActivityPhase,
                w.ActivityMessage,
                w.ActivityAccountId,
                w.ActivityAccountName,
                w.ActivitySubProfileId,
                w.ActivitySubProfileName,
                w.ActivityUpdatedAtUtc,
                w.ActivityNextCycleAtUtc,
                w.ActivityActiveAccountsJson
            })
            .ToListAsync(ct);

        var workerIds = workers.Select(w => w.Id).ToList();
        var operationalStats = await ComputeWorkerOperationalStatsAsync(workerIds, todayStart, ct);

        var items = workers.Select(w =>
        {
            operationalStats.TryGetValue(w.Id, out var op);
            op ??= WorkerOperationalStats.Empty;
            return new WorkerListItem(
                w.Id,
                w.DisplayName,
                w.MachineName,
                w.AppVersion,
                w.MonitoringStatus,
                w.MonitoringStatusMessage,
                w.IsMonitoringActive,
                WorkerOnlineRules.IsOnline(w.LastSeenAtUtc, nowUtc, connectionRegistry.IsConnected(w.Id)),
                w.LastSeenAtUtc,
                op.TotalAccounts,
                op.TodayResponses,
                op.TodayDuplicates,
                op.TodayEventErrors,
                false,
                null,
                w.OfficeId,
                w.OfficeName,
                w.IsEnabled,
                op.ActiveAccounts,
                op.LowBalanceAccountCount,
                WorkerActivityMapper.ToDto(
                    w.ActivityPhase,
                    w.ActivityMessage,
                    w.ActivityAccountId,
                    w.ActivityAccountName,
                    w.ActivitySubProfileId,
                    w.ActivitySubProfileName,
                    w.ActivityNextCycleAtUtc,
                    w.ActivityUpdatedAtUtc,
                    WorkerActivityMapper.DeserializeActiveAccounts(w.ActivityActiveAccountsJson)),
                WorkerActivityMapper.DeserializeActiveAccounts(w.ActivityActiveAccountsJson),
                w.IsMonitoringPaused,
                w.IpAddress ?? string.Empty);
        }).ToList();

        var tabCounts = DashboardWorkerFilter.Count(items);
        var selectedItems = items
            .Where(item => DashboardWorkerFilter.Matches(item, DashboardWorkerFilter.Normalize(workerFilter)))
            .ToList();
        var normalizedPage = WorkerListPaging.NormalizePage(page, normalizedPageSize, selectedItems.Count);
        var pageItems = selectedItems
            .OrderByDescending(x => x.LowBalanceAccountCount > 0)
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToList();

        return new WorkersPageDto(
            pageItems,
            selectedItems.Count,
            normalizedPage,
            normalizedPageSize,
            sortColumn,
            sortDescending ? "desc" : "asc",
            enabledCount,
            pausedCount)
        {
            TabCounts = tabCounts
        };
    }

    private IOrderedQueryable<WorkerEntity> ApplyWorkerListSort(
        IQueryable<WorkerEntity> query,
        string sort,
        bool descending,
        DateTime todayStartUtc)
    {
        if (string.Equals(sort, "responses", StringComparison.OrdinalIgnoreCase))
        {
            return descending
                ? query.OrderByDescending(worker => db.CandidateResponses.Count(response =>
                    response.WorkerId == worker.Id && response.CollectedAt >= todayStartUtc))
                    .ThenByDescending(worker => worker.DisplayName)
                    .ThenByDescending(worker => worker.Id)
                : query.OrderBy(worker => db.CandidateResponses.Count(response =>
                    response.WorkerId == worker.Id && response.CollectedAt >= todayStartUtc))
                    .ThenBy(worker => worker.DisplayName)
                    .ThenBy(worker => worker.Id);
        }

        if (string.Equals(sort, "errors", StringComparison.OrdinalIgnoreCase))
        {
            return descending
                ? query.OrderByDescending(worker => db.WorkerEvents.Count(error =>
                    error.WorkerId == worker.Id
                    && !error.IsDismissed
                    && error.CreatedAtUtc >= todayStartUtc
                    && (error.Level == "Error" || error.Level == "Warning")))
                    .ThenByDescending(worker => worker.DisplayName)
                    .ThenByDescending(worker => worker.Id)
                : query.OrderBy(worker => db.WorkerEvents.Count(error =>
                    error.WorkerId == worker.Id
                    && !error.IsDismissed
                    && error.CreatedAtUtc >= todayStartUtc
                    && (error.Level == "Error" || error.Level == "Warning")))
                    .ThenBy(worker => worker.DisplayName)
                    .ThenBy(worker => worker.Id);
        }

        return sort.ToLowerInvariant() switch
        {
            "activity" => descending
                ? query.OrderByDescending(x => x.LastSeenAtUtc ?? DateTime.MinValue)
                    .ThenByDescending(x => x.DisplayName)
                    .ThenByDescending(x => x.Id)
                : query.OrderBy(x => x.LastSeenAtUtc ?? DateTime.MaxValue)
                    .ThenBy(x => x.DisplayName)
                    .ThenBy(x => x.Id),
            _ => descending
                ? query.OrderByDescending(x => x.DisplayName).ThenByDescending(x => x.Id)
                : query.OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
        };
    }

    public async Task<WorkerDetail?> GetWorkerDetailAsync(
        Guid workerId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return null;
        }

        Task<WorkerDetail?> Load(CancellationToken token) => GetWorkerDetailUncachedAsync(workerId, token);
        var detail = queryCache is null
            ? await Load(ct)
            : await queryCache.GetOrCreateAsync(
                OrbitaCacheDomain.WorkerDetails,
                scope.ResolveFilter(null),
                ScopeAudience(scope),
                new { Kind = "worker-detail", workerId },
                OrbitaCachePolicy.Realtime,
                Load,
                ct);
        if (detail is null)
        {
            return null;
        }

        // Secrets and process-local login state must never enter either cache tier.
        // This indexed lookup is deliberately small compared with the aggregate
        // snapshot/account queries cached above.
        var requestOnly = await db.Workers.AsNoTracking()
            .Where(x => x.Id == workerId)
            .Select(x => new { x.AdsPowerApiKey, x.RuCaptchaApiKey })
            .FirstOrDefaultAsync(ct);
        if (requestOnly is null)
        {
            return null;
        }

        return detail with
        {
            AdsPowerApiKey = requestOnly.AdsPowerApiKey,
            RuCaptchaApiKey = requestOnly.RuCaptchaApiKey,
            PendingLocalChromeLoginAccountId = localChromeLoginSessions?.GetPendingForWorker(workerId)?.AccountId
        };
    }

    private async Task<WorkerDetail?> GetWorkerDetailUncachedAsync(
        Guid workerId,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var worker = await db.Workers.AsNoTracking()
            .Include(x => x.Office)
            .FirstOrDefaultAsync(x => x.Id == workerId, ct);
        if (worker is null)
        {
            return null;
        }

        var latestSnapshot = await db.WorkerSnapshots
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId)
            .OrderByDescending(x => x.CapturedAtUtc)
            .FirstOrDefaultAsync(ct);

        DashboardStatsDto? stats = null;
        List<WorkerBalanceDto> balances = [];
        if (latestSnapshot is not null)
        {
            stats = JsonSerializer.Deserialize<DashboardStatsDto>(latestSnapshot.StatsJson, JsonOptions);
            balances = JsonSerializer.Deserialize<List<WorkerBalanceDto>>(latestSnapshot.BalancesJson, JsonOptions) ?? [];
            balances = await EnrichBalancesFromAccountsAsync(workerId, balances, ct);
        }

        var todayStart = nowUtc.Date;
        var todayEndUtc = LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(DateTime.Today).UtcEndExclusive;
        var workerEventErrors = await ComputeWorkerEventErrorStatsAsync(
            new HashSet<Guid> { workerId },
            todayStart,
            todayStart,
            ct);
        var workerSendTimestamps = await LoadSendTimestampsAsync(
            new HashSet<Guid> { workerId },
            todayStart,
            todayEndUtc,
            ct);
        var hourlyActivity = await ComputeHourlyActivityFromDbAsync(
            new HashSet<Guid> { workerId },
            todayStart,
            workerSendTimestamps,
            workerEventErrors.Hourly,
            ct);
        if (stats is not null)
        {
            stats = stats with { HourlyActivity = hourlyActivity };
        }

        var operationalStats = await ComputeWorkerOperationalStatsAsync([workerId], todayStart, ct);
        operationalStats.TryGetValue(workerId, out var op);
        op ??= WorkerOperationalStats.Empty;

        return new WorkerDetail(
            worker.Id,
            worker.DisplayName,
            worker.MachineName,
            worker.AppVersion,
            worker.MonitoringStatus,
            worker.MonitoringStatusMessage,
            worker.IsMonitoringActive,
            WorkerOnlineRules.IsOnline(worker.LastSeenAtUtc, nowUtc, connectionRegistry.IsConnected(worker.Id)),
            worker.LastSeenAtUtc,
            worker.NextCycleCheckAtUtc,
            stats,
            balances,
            worker.MaxConcurrentAccounts,
            worker.LastCpuPercent,
            worker.LastRamPercent,
            worker.LastRamUsedMb,
            worker.LastRamTotalMb,
            worker.IpAddress,
            worker.OperatingSystem,
            worker.StartedAtUtc,
            worker.AgentVersion,
            worker.AdsPowerApiBaseUrl,
            AdsPowerApiKey: null,
            worker.IsEnabled,
            op.TodayResponses,
            op.TodayDuplicates,
            op.TodayEventErrors,
            op.ActiveAccounts,
            op.TotalAccounts,
            WorkerActivityMapper.ToDto(worker),
            WorkerActivityMapper.DeserializeActiveAccounts(worker.ActivityActiveAccountsJson),
            worker.ResponseFilterEnabled,
            worker.ResponseFilterExcludeFemale,
            worker.ResponseFilterMaxAge,
            worker.ResponseFilterExcludeMale,
            worker.ResponseFilterMaxAgeMale,
            worker.ResponseFilterMaxAgeFemale,
            worker.ResponseFilterMaxAgeDays,
            worker.ResponseHighlightEnabled,
            worker.ResponseHighlightAgeBuckets,
            worker.AutoScheduleEnabled,
            worker.AutoScheduleDays,
            worker.AutoScheduleFromLocalTime,
            worker.AutoScheduleToLocalTime,
            worker.MessengerAutoReplyEnabled,
            worker.MessengerAutoReplyMessage,
            worker.PhoneUnchangedHours,
            worker.AutoDeliverToCrm,
            worker.AutoDeliverToBitrix,
            worker.OfficeId,
            worker.Office?.Name ?? string.Empty,
            worker.ResponseHighlightTargetsJson,
            worker.AdsPowerGroupId,
            worker.AdsPowerGroupName,
            AdsPowerGroupsJson.Parse(worker.AdsPowerGroupsJson),
            RuCaptchaApiKey: null,
            worker.MultiloginLauncherUrl,
            worker.MultiloginCloudApiUrl,
            HasMultiloginAutomationToken: !string.IsNullOrWhiteSpace(worker.MultiloginAutomationToken),
            LocalChromeExecutablePath: worker.LocalChromeExecutablePath,
            AdsPowerEnabled: worker.AdsPowerEnabled,
            MultiloginEnabled: worker.MultiloginEnabled,
            LocalChromeEnabled: worker.LocalChromeEnabled,
            AdsPowerCheck: WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.AdsPower),
            MultiloginCheck: WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.Multilogin),
            LocalChromeCheck: WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.Local),
            PendingLocalChromeLoginAccountId: null,
            IsMonitoringPaused: worker.IsMonitoringPaused);
    }

    public async Task<IReadOnlyList<WorkerAccountDto>> GetWorkerAccountsAsync(
        Guid workerId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return [];
        }

        // Keep the public return type broad while storing a concrete array.
        // The account graph is large and nested, so it uses the query cache's
        // direct Redis payload path rather than HybridCache collection hydration.
        async Task<WorkerAccountDto[]> Load(CancellationToken token) =>
            [.. await LoadWorkerAccountsAsync(workerId, token).ConfigureAwait(false)];
        return queryCache is null
            ? await Load(ct)
            : await queryCache.GetOrCreateDistributedAsync(
                OrbitaCacheDomain.WorkerDetails,
                scope.ResolveFilter(null),
                ScopeAudience(scope),
                new { Kind = "worker-accounts", CacheFormat = 2, workerId },
                OrbitaCachePolicy.LargeRealtime,
                Load,
                ct);
    }

    private async Task<IReadOnlyList<WorkerAccountDto>> LoadWorkerAccountsAsync(
        Guid workerId,
        CancellationToken ct)
    {
        var byWorker = await LoadAccountsByWorkerAsync([workerId], ct);
        return byWorker.TryGetValue(workerId, out var accounts) ? accounts : [];
    }

    public async Task<IReadOnlyList<OfficeAccountListItem>> GetOfficeAccountsAsync(
        OfficeScope scope,
        Guid? officeFilter = null,
        Guid? workerId = null,
        CancellationToken ct = default)
    {
        if (workerId is Guid requestedWorkerId
            && !await officeScope.CanAccessWorkerAsync(scope, requestedWorkerId, ct))
        {
            return [];
        }

        return queryCache is null
            ? await PanelAggregateCache.GetOrCreateAsync(
                PanelAggregateCache.AccountsKey(scope, officeFilter, workerId),
                PanelAggregateCache.AccountsTtl,
                () => ComputeOfficeAccountsCoreAsync(scope, officeFilter, workerId, ct))
            : await queryCache.GetOrCreateAsync(
                OrbitaCacheDomain.Dashboard,
                scope.ResolveFilter(officeFilter),
                ScopeAudience(scope),
                new { Kind = "office-accounts", workerId },
                OrbitaCachePolicy.Realtime,
                token => ComputeOfficeAccountsCoreAsync(scope, officeFilter, workerId, token),
                ct);
    }

    private async Task<IReadOnlyList<OfficeAccountListItem>> ComputeOfficeAccountsCoreAsync(
        OfficeScope scope,
        Guid? officeFilter,
        Guid? workerId,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var workersQuery = officeScope
            .ApplyWorkerFilter(db.Workers.AsNoTracking(), scope, officeFilter)
            .Where(x => x.MachineName != LeadFlowImportWorker.MachineName);
        if (workerId is Guid requestedWorkerId)
        {
            workersQuery = workersQuery.Where(x => x.Id == requestedWorkerId);
        }

        var workers = await workersQuery
            .OrderBy(x => x.DisplayName)
            .Select(w => new
            {
                w.Id,
                w.DisplayName,
                OfficeName = w.Office.Name,
                w.LastSeenAtUtc,
                w.ActivityPhase,
                w.ActivityMessage,
                w.ActivityAccountId,
                w.ActivityAccountName,
                w.ActivitySubProfileId,
                w.ActivitySubProfileName,
                w.ActivityUpdatedAtUtc,
                w.ActivityNextCycleAtUtc,
                w.ActivityActiveAccountsJson
            })
            .ToListAsync(ct);

        if (workers.Count == 0)
        {
            return [];
        }

        var workerIds = workers.Select(w => w.Id).ToList();
        var accountsByWorker = await LoadAccountsByWorkerAsync(workerIds, ct);
        var balancesByAccount = await LoadLatestBalancesForWorkersAsync(workerIds, ct);

        var items = new List<OfficeAccountListItem>();
        foreach (var worker in workers)
        {
            if (!accountsByWorker.TryGetValue(worker.Id, out var accounts) || accounts.Count == 0)
            {
                continue;
            }

            var isOnline = WorkerOnlineRules.IsOnline(
                worker.LastSeenAtUtc,
                nowUtc,
                connectionRegistry.IsConnected(worker.Id));
            var activeAccounts = WorkerActivityMapper.DeserializeActiveAccounts(worker.ActivityActiveAccountsJson);
            var activity = WorkerActivityMapper.ToDto(
                worker.ActivityPhase,
                worker.ActivityMessage,
                worker.ActivityAccountId,
                worker.ActivityAccountName,
                worker.ActivitySubProfileId,
                worker.ActivitySubProfileName,
                worker.ActivityNextCycleAtUtc,
                worker.ActivityUpdatedAtUtc,
                activeAccounts);

            foreach (var account in accounts)
            {
                balancesByAccount.TryGetValue((worker.Id, account.AccountId), out var balance);
                items.Add(new OfficeAccountListItem(
                    worker.Id,
                    worker.DisplayName,
                    worker.OfficeName,
                    isOnline,
                    activity,
                    activeAccounts,
                    account,
                    balance));
            }
        }

        return items;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<WorkerAccountDto>>> LoadAccountsByWorkerAsync(
        IReadOnlyList<Guid> workerIds,
        CancellationToken ct)
    {
        var result = workerIds.ToDictionary(
            static id => id,
            static _ => (IReadOnlyList<WorkerAccountDto>)[]);
        if (workerIds.Count == 0)
        {
            return result;
        }

        var workerIdList = workerIds as List<Guid> ?? workerIds.ToList();
        var todayStart = DateTime.UtcNow.Date;
        var rows = await db.WorkerAccounts
            .AsNoTracking()
            .Where(x => workerIdList.Contains(x.WorkerId))
            .OrderBy(x => x.DisplayName)
            .Select(x => new
            {
                x.WorkerId,
                x.AccountId,
                x.DisplayName,
                x.Status,
                x.IsEnabledInPanel,
                x.ActiveAdsCount,
                x.BlockedCount,
                x.DraftsCount,
                x.LastErrorMessage,
                x.LastMonitoringAt,
                x.AdsPowerProfileId,
                x.AdsPowerGroupId,
                x.AdsPowerGroupName,
                x.MultiloginProfileId,
                x.MultiloginFolderId,
                x.LocalUserDataDir,
                x.SubProfilesJson,
                x.SubProfilesRefreshedAtUtc,
                x.SubProfilesRefreshRequestedAtUtc,
                x.SubProfilesDisabledIdsJson,
                x.AvitoLogin,
                x.AvitoPasswordProtected,
                x.LocalProxyEnabled,
                x.LocalProxyAddress,
                x.LocalProxyUsername,
                x.LocalProxyPasswordProtected,
                x.LocalTrafficMode,
                x.LocalBlockMedia,
                x.LocalBlockAnalytics,
                x.LocalBlockImages,
                x.LocalBlockFonts,
                x.LocalBlockPrefetch,
                x.LocalNavigationTimeoutSeconds,
                x.LocalTrafficLastNavigationMs,
                x.LocalTrafficBlockedMedia,
                x.LocalTrafficBlockedImages,
                x.LocalTrafficBlockedFonts,
                x.LocalTrafficBlockedAnalytics,
                x.LocalTrafficBlockedPrefetch,
                x.TotalBalance
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return result;
        }

        var accountIds = rows.Select(x => x.AccountId).Distinct().ToList();

        var responseStats = (await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.WorkerId != null
                && workerIdList.Contains(x.WorkerId.Value)
                && accountIds.Contains(x.AccountId)
                && x.CollectedAt >= todayStart)
            .GroupBy(x => new { WorkerId = x.WorkerId!.Value, x.AccountId })
            .Select(g => new
            {
                g.Key.WorkerId,
                g.Key.AccountId,
                Total = g.Count(),
                Duplicates = g.Count(x => x.Status == ResponseStatuses.Duplicate)
            })
            .ToListAsync(ct))
            .ToDictionary(x => (x.WorkerId, x.AccountId), x => (x.Total, x.Duplicates));

        var eventStats = (await db.WorkerEvents
            .AsNoTracking()
            .Where(x => workerIdList.Contains(x.WorkerId)
                && x.AccountId != null
                && accountIds.Contains(x.AccountId.Value)
                && !x.IsDismissed
                && x.CreatedAtUtc >= todayStart
                && (x.Level == "Error" || x.Level == "Warning"))
            .GroupBy(x => new { x.WorkerId, AccountId = x.AccountId!.Value })
            .Select(g => new { g.Key.WorkerId, g.Key.AccountId, Count = g.Count() })
            .ToListAsync(ct))
            .ToDictionary(x => (x.WorkerId, x.AccountId), x => x.Count);

        var subProfileResponseRows = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.WorkerId != null
                && workerIdList.Contains(x.WorkerId.Value)
                && accountIds.Contains(x.AccountId)
                && x.CollectedAt >= todayStart
                && x.AvitoSubProfileId != "")
            .GroupBy(x => new { WorkerId = x.WorkerId!.Value, x.AccountId, x.AvitoSubProfileId })
            .Select(g => new
            {
                g.Key.WorkerId,
                g.Key.AccountId,
                g.Key.AvitoSubProfileId,
                Total = g.Count(),
                Duplicates = g.Count(x => x.Status == ResponseStatuses.Duplicate),
                Errors = g.Count(x => x.Status == ResponseStatuses.Error || x.Status == ResponseStatuses.ActionRequired),
                LastActivity = g.Max(x => (DateTime?)x.CollectedAt)
            })
            .ToListAsync(ct);

        var subProfileAllTimeActivityRows = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.WorkerId != null
                && workerIdList.Contains(x.WorkerId.Value)
                && accountIds.Contains(x.AccountId)
                && x.AvitoSubProfileId != "")
            .GroupBy(x => new { WorkerId = x.WorkerId!.Value, x.AccountId, x.AvitoSubProfileId })
            .Select(g => new
            {
                g.Key.WorkerId,
                g.Key.AccountId,
                g.Key.AvitoSubProfileId,
                LastActivity = g.Max(x => (DateTime?)x.CollectedAt)
            })
            .ToListAsync(ct);

        var subProfileNameRows = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.WorkerId != null
                && workerIdList.Contains(x.WorkerId.Value)
                && accountIds.Contains(x.AccountId)
                && x.AvitoSubProfileId != "")
            .GroupBy(x => new { WorkerId = x.WorkerId!.Value, x.AccountId, x.AvitoSubProfileId })
            .Select(g => new
            {
                g.Key.WorkerId,
                g.Key.AccountId,
                g.Key.AvitoSubProfileId,
                Name = g.OrderByDescending(x => x.CreatedAt)
                    .Select(x => x.AvitoSubProfileName)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var lastEventByAccount = (await db.WorkerEvents
            .AsNoTracking()
            .Where(x => workerIdList.Contains(x.WorkerId)
                && x.AccountId != null
                && accountIds.Contains(x.AccountId.Value)
                && !x.IsDismissed)
            .GroupBy(x => new { x.WorkerId, AccountId = x.AccountId!.Value })
            .Select(g => new { g.Key.WorkerId, g.Key.AccountId, LastAt = g.Max(x => x.CreatedAtUtc) })
            .ToListAsync(ct))
            .ToDictionary(x => (x.WorkerId, x.AccountId), x => x.LastAt);

        var lastResponseByAccount = (await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.WorkerId != null
                && workerIdList.Contains(x.WorkerId.Value)
                && accountIds.Contains(x.AccountId))
            .GroupBy(x => new { WorkerId = x.WorkerId!.Value, x.AccountId })
            .Select(g => new { g.Key.WorkerId, g.Key.AccountId, LastAt = g.Max(x => x.CollectedAt) })
            .ToListAsync(ct))
            .ToDictionary(x => (x.WorkerId, x.AccountId), x => x.LastAt);

        var subProfileEventRows = await db.WorkerEvents
            .AsNoTracking()
            .Where(x => workerIdList.Contains(x.WorkerId)
                && x.AccountId != null
                && accountIds.Contains(x.AccountId.Value)
                && !x.IsDismissed)
            .Select(x => new
            {
                x.WorkerId,
                AccountId = x.AccountId!.Value,
                x.Details,
                x.CreatedAtUtc,
                x.Level
            })
            .ToListAsync(ct);

        var deserializedProfiles = rows
            .Select(x => (
                x.WorkerId,
                x.AccountId,
                Profiles: DeserializeSubProfiles(x.SubProfilesJson, x.SubProfilesDisabledIdsJson)))
            .ToList();

        var referencedAttachmentIds = SubProfileIssueHelper.CollectAttachmentIds(
            deserializedProfiles.SelectMany(x => x.Profiles ?? []));
        var existingAttachmentIds = referencedAttachmentIds.Count == 0
            ? new HashSet<Guid>()
            : await db.WorkerDiagnosticAttachments
                .AsNoTracking()
                .Where(x => referencedAttachmentIds.Contains(x.Id))
                .Select(x => x.Id)
                .ToHashSetAsync(ct);

        foreach (var workerId in workerIds)
        {
            var workerRows = rows.Where(x => x.WorkerId == workerId).ToList();
            if (workerRows.Count == 0)
            {
                continue;
            }

            var workerProfiles = deserializedProfiles
                .Where(x => x.WorkerId == workerId)
                .Select(x => (x.AccountId, x.Profiles))
                .ToList();

            var subProfileStats = SubProfileOperationalStatsHelper.MergeResponseStats(
                subProfileResponseRows
                    .Where(x => x.WorkerId == workerId)
                    .Select(x => (
                        x.AccountId,
                        x.AvitoSubProfileId,
                        x.Total,
                        x.Duplicates,
                        x.Errors,
                        LastActivity: (DateTime?)null)));

            SubProfileOperationalStatsHelper.MergeAllTimeActivity(
                subProfileStats,
                subProfileAllTimeActivityRows
                    .Where(x => x.WorkerId == workerId)
                    .Select(x => (x.AccountId, x.AvitoSubProfileId, x.LastActivity)));

            var responseNameLookup = SubProfileOperationalStatsHelper.BuildResponseNameLookup(
                subProfileNameRows
                    .Where(x => x.WorkerId == workerId)
                    .Select(x => (x.AccountId, x.AvitoSubProfileId, (string?)x.Name)));

            SubProfileOperationalStatsHelper.ApplyEvents(
                subProfileStats,
                subProfileEventRows
                    .Where(x => x.WorkerId == workerId)
                    .Select(x => (x.AccountId, x.Details, x.CreatedAtUtc, x.Level)),
                SubProfileOperationalStatsHelper.BuildNameLookup(workerProfiles),
                todayStart);

            var accountDtos = workerRows
                .Select(x =>
                {
                    responseStats.TryGetValue((x.WorkerId, x.AccountId), out var responses);
                    eventStats.TryGetValue((x.WorkerId, x.AccountId), out var eventErrors);
                    lastEventByAccount.TryGetValue((x.WorkerId, x.AccountId), out var lastEventAt);
                    lastResponseByAccount.TryGetValue((x.WorkerId, x.AccountId), out var lastResponseAt);
                    var subProfiles = SubProfileOperationalStatsHelper.Resolve(
                        SubProfileIssueHelper.StripMissingAttachmentIssues(
                            DeserializeSubProfiles(x.SubProfilesJson, x.SubProfilesDisabledIdsJson),
                            existingAttachmentIds),
                        x.AccountId,
                        subProfileStats,
                        responseNameLookup);
                    var hasCredentials = !string.IsNullOrWhiteSpace(x.AvitoLogin)
                        && !string.IsNullOrWhiteSpace(x.AvitoPasswordProtected);
                    var isLocal = !string.IsNullOrWhiteSpace(x.LocalUserDataDir)
                        && string.IsNullOrWhiteSpace(x.MultiloginProfileId)
                        && string.IsNullOrWhiteSpace(x.AdsPowerProfileId);
                    var proxyEnabled = isLocal && x.LocalProxyEnabled;
                    var proxyAddress = isLocal && !string.IsNullOrWhiteSpace(x.LocalProxyAddress)
                        ? x.LocalProxyAddress.Trim()
                        : null;
                    var traffic = isLocal
                        ? LocalChromeTrafficRules.FromStored(
                            x.LocalTrafficMode,
                            x.LocalBlockMedia,
                            x.LocalBlockAnalytics,
                            x.LocalBlockImages,
                            x.LocalBlockFonts,
                            x.LocalBlockPrefetch,
                            x.LocalNavigationTimeoutSeconds)
                        : LocalChromeTrafficRules.Normal;
                    return new WorkerAccountDto(
                        x.AccountId,
                        x.DisplayName,
                        x.Status,
                        x.IsEnabledInPanel,
                        x.ActiveAdsCount,
                        x.BlockedCount,
                        x.DraftsCount,
                        x.LastErrorMessage,
                        x.LastMonitoringAt,
                        x.IsEnabledInPanel,
                        x.AdsPowerProfileId,
                        subProfiles,
                        x.SubProfilesRefreshedAtUtc,
                        x.SubProfilesRefreshRequestedAtUtc,
                        responses.Total,
                        responses.Duplicates,
                        eventErrors,
                        AccountLastActivityHelper.Resolve(
                            x.LastMonitoringAt,
                            lastEventAt,
                            lastResponseAt),
                        hasCredentials,
                        string.IsNullOrWhiteSpace(x.AvitoLogin) ? null : x.AvitoLogin.Trim(),
                        x.AdsPowerGroupId,
                        x.AdsPowerGroupName,
                        string.IsNullOrWhiteSpace(x.MultiloginProfileId) ? null : x.MultiloginProfileId,
                        string.IsNullOrWhiteSpace(x.MultiloginFolderId) ? null : x.MultiloginFolderId,
                        string.IsNullOrWhiteSpace(x.LocalUserDataDir) ? null : x.LocalUserDataDir,
                        proxyEnabled,
                        proxyAddress,
                        isLocal && !string.IsNullOrWhiteSpace(x.LocalProxyUsername)
                            ? x.LocalProxyUsername.Trim()
                            : null,
                        isLocal && !string.IsNullOrWhiteSpace(x.LocalProxyPasswordProtected),
                        isLocal ? traffic.Mode : null,
                        isLocal && traffic.BlockMedia,
                        isLocal && traffic.BlockAnalytics,
                        isLocal && traffic.BlockImages,
                        isLocal && traffic.BlockFonts,
                        isLocal && traffic.BlockPrefetch,
                        isLocal ? traffic.NavigationTimeoutSeconds : LocalChromeTrafficRules.DefaultTimeoutSeconds,
                        isLocal ? x.LocalTrafficLastNavigationMs : null,
                        isLocal ? x.LocalTrafficBlockedMedia : 0,
                        isLocal ? x.LocalTrafficBlockedImages : 0,
                        isLocal ? x.LocalTrafficBlockedFonts : 0,
                        isLocal ? x.LocalTrafficBlockedAnalytics : 0,
                        isLocal ? x.LocalTrafficBlockedPrefetch : 0,
                        isLocal
                            ? LocalChromeTrafficRules.FormatLastRun(
                                x.LocalTrafficLastNavigationMs,
                                x.LocalTrafficBlockedMedia,
                                x.LocalTrafficBlockedImages,
                                x.LocalTrafficBlockedFonts,
                                x.LocalTrafficBlockedAnalytics,
                                x.LocalTrafficBlockedPrefetch)
                            : null);
                })
                .ToList();

            await TryBackfillSubProfilesFromResponsesAsync(
                workerId,
                workerRows
                    .Where(x => string.IsNullOrWhiteSpace(x.SubProfilesJson) || x.SubProfilesJson == "[]")
                    .Select(x => x.AccountId)
                    .ToList(),
                accountDtos,
                ct);

            result[workerId] = accountDtos;
        }

        return result;
    }

    public async Task<IReadOnlyList<WorkerEventListItem>> GetWorkerEventsAsync(
        OfficeScope scope,
        Guid? workerId,
        Guid? officeFilter,
        int limit,
        DateTime? sinceUtc = null,
        CancellationToken ct = default)
    {
        if (workerId.HasValue && !await officeScope.CanAccessWorkerAsync(scope, workerId.Value, ct))
        {
            return [];
        }

        var workersQuery = officeScope
            .ApplyWorkerFilter(db.Workers.AsNoTracking(), scope, officeFilter)
            .Where(x => x.MachineName != LeadFlowImportWorker.MachineName);
        var allowedWorkerIds = await workersQuery.Select(x => x.Id).ToListAsync(ct);

        var query = db.WorkerEvents.AsNoTracking()
            .Where(x => allowedWorkerIds.Contains(x.WorkerId) && !x.IsDismissed);
        if (workerId.HasValue)
        {
            query = query.Where(x => x.WorkerId == workerId.Value);
        }

        if (sinceUtc.HasValue)
        {
            query = query.Where(x => x.CreatedAtUtc >= sinceUtc.Value);
        }

        return await (
            from evt in query.OrderByDescending(x => x.CreatedAtUtc).Take(limit)
            join worker in db.Workers.AsNoTracking() on evt.WorkerId equals worker.Id
            join account in db.WorkerAccounts.AsNoTracking()
                on new { evt.WorkerId, AccountId = evt.AccountId }
                equals new { account.WorkerId, AccountId = (Guid?)account.AccountId }
                into accounts
            from account in accounts.DefaultIfEmpty()
            select new WorkerEventListItem(
                evt.Id,
                evt.WorkerId,
                worker.DisplayName,
                evt.AccountId,
                account != null ? account.DisplayName : null,
                evt.Level,
                evt.Message,
                evt.Details,
                evt.CreatedAtUtc))
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<ActivityPointDto>> ComputeDailyActivityFromDbAsync(
        HashSet<Guid> workerIds,
        DateTime startLocal,
        DateTime endLocal,
        IReadOnlyList<DateTime> sendTimestamps,
        int? timeZoneOffsetMinutes,
        CancellationToken ct)
    {
        var (rangeStart, rangeEnd, utcStart, utcEnd) = LocalCalendarDateRange.Normalize(
            startLocal,
            endLocal,
            timeZoneOffsetMinutes);
        if (workerIds.Count == 0)
        {
            return BuildEmptyDailyActivity(rangeStart, rangeEnd);
        }

        var rows = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.WorkerId != null
                && workerIds.Contains(x.WorkerId.Value)
                && x.CollectedAt >= utcStart
                && x.CollectedAt < utcEnd)
            .Select(x => new { x.CollectedAt, x.Status })
            .ToListAsync(ct);

        var byDay = new Dictionary<DateTime, DailyResponseCounters>();
        foreach (var row in rows)
        {
            var localDate = LocalCalendarDateRange.ToLocalDateFromStoredUtc(row.CollectedAt, timeZoneOffsetMinutes);
            if (!byDay.TryGetValue(localDate, out var bucket))
            {
                bucket = new DailyResponseCounters();
                byDay[localDate] = bucket;
            }

            bucket.Total++;
            if (row.Status == ResponseStatuses.Duplicate)
            {
                bucket.Duplicates++;
            }
        }

        // "Sent" by actual send date (CRM + Bitrix), not response collection date.
        foreach (var sentAtUtc in sendTimestamps)
        {
            var localDate = LocalCalendarDateRange.ToLocalDateFromStoredUtc(sentAtUtc, timeZoneOffsetMinutes);
            if (localDate < startLocal.Date || localDate > endLocal.Date)
            {
                continue;
            }

            if (!byDay.TryGetValue(localDate, out var bucket))
            {
                bucket = new DailyResponseCounters();
                byDay[localDate] = bucket;
            }

            bucket.Sent++;
        }

        return BuildDailyActivity(rangeStart, rangeEnd, byDay);
    }

    private static IReadOnlyList<ActivityPointDto> BuildEmptyDailyActivity(DateTime startLocal, DateTime endLocal)
    {
        return BuildDailyActivity(startLocal, endLocal, new Dictionary<DateTime, DailyResponseCounters>());
    }

    private static IReadOnlyList<ActivityPointDto> BuildDailyActivity(
        DateTime startLocal,
        DateTime endLocal,
        IReadOnlyDictionary<DateTime, DailyResponseCounters> byDay)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var list = new List<ActivityPointDto>((endLocal - startLocal).Days + 1);
        for (var day = startLocal; day <= endLocal; day = day.AddDays(1))
        {
            byDay.TryGetValue(day, out var bucket);
            bucket ??= new DailyResponseCounters();
            list.Add(new ActivityPointDto(
                day.ToString("ddd d.MM", ru),
                bucket.Total,
                bucket.Sent,
                bucket.Duplicates,
                0,
                0,
                1,
                day));
        }

        return list;
    }

    private sealed class DailyResponseCounters
    {
        public int Total { get; set; }
        public int Sent { get; set; }
        public int Duplicates { get; set; }
    }

    private async Task<IReadOnlyList<ActivityPointDto>> ComputeHourlyActivityFromDbAsync(
        HashSet<Guid> workerIds,
        DateTime todayStartUtc,
        IReadOnlyList<DateTime> sendTimestamps,
        IReadOnlyList<int> hourlyEventErrors,
        CancellationToken ct)
    {
        var newCounts = new int[24];
        var sentCounts = new int[24];
        var duplicateCounts = new int[24];

        if (workerIds.Count > 0)
        {
            var rows = await db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.WorkerId != null && workerIds.Contains(x.WorkerId.Value) && x.CollectedAt >= todayStartUtc)
                .Select(x => new { x.CollectedAt, x.Status })
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                var hour = UtcHour(row.CollectedAt);
                if (hour is < 0 or > 23)
                {
                    continue;
                }

                newCounts[hour]++;
                if (row.Status == ResponseStatuses.Duplicate)
                {
                    duplicateCounts[hour]++;
                }
            }
        }

        // "Sent" by actual send hour (CRM + Bitrix), not response collection hour.
        foreach (var sentAtUtc in sendTimestamps)
        {
            if (sentAtUtc < todayStartUtc)
            {
                continue;
            }

            var hour = UtcHour(sentAtUtc);
            if (hour is < 0 or > 23)
            {
                continue;
            }

            sentCounts[hour]++;
        }

        var errorHourly = hourlyEventErrors.Count == 24
            ? hourlyEventErrors
            : Enumerable.Repeat(0, 24).ToArray();

        return Enumerable.Range(0, 24)
            .Select(hour => new ActivityPointDto(
                $"{hour:00}:00",
                newCounts[hour],
                sentCounts[hour],
                duplicateCounts[hour],
                errorHourly[hour],
                hour,
                1,
                null))
            .ToArray();
    }

    private static int UtcHour(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return utc.Hour;
    }

    private static IReadOnlyList<ActivityPointDto> AggregateHourly(IReadOnlyList<DashboardStatsDto> statsList)
    {
        var buckets = new ActivityPointDto[24];
        for (var hour = 0; hour < 24; hour++)
        {
            buckets[hour] = new ActivityPointDto($"{hour:00}:00", 0, 0, 0, 0, hour, 1, null);
        }

        foreach (var stats in statsList)
        {
            foreach (var point in stats.HourlyActivity)
            {
                if (point.SlotStartHour is < 0 or > 23)
                {
                    continue;
                }

                var bucket = buckets[point.SlotStartHour];
                buckets[point.SlotStartHour] = bucket with
                {
                    NewCount = bucket.NewCount + point.NewCount,
                    SentCount = bucket.SentCount + point.SentCount,
                    DuplicateCount = bucket.DuplicateCount + point.DuplicateCount,
                    ErrorCount = bucket.ErrorCount + point.ErrorCount
                };
            }
        }

        return buckets;
    }

    // Computes today response aggregates directly from CandidateResponses (source of truth).
    // "Sent" is not derived here — it uses actual send timestamps (CRM + Bitrix deliveries).
    private async Task<(int TotalToday, int Duplicates, int Errors, int InProgress, int ActionRequired)>
        ComputeTodayResponseStatsAsync(HashSet<Guid> workerIds, DateTime todayStartUtc, CancellationToken ct)
    {
        if (workerIds.Count == 0)
            return (0, 0, 0, 0, 0);

        var query = db.CandidateResponses.AsNoTracking()
            .Where(x => x.WorkerId != null && workerIds.Contains(x.WorkerId.Value) && x.CollectedAt >= todayStartUtc);

        var statusCounts = await query
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        if (statusCounts.Count == 0)
        {
            return (0, 0, 0, 0, 0);
        }

        int CountFor(string status) =>
            statusCounts.FirstOrDefault(x => x.Status == status)?.Count ?? 0;

        var totalToday = statusCounts.Sum(x => x.Count);
        var duplicates = CountFor(ResponseStatuses.Duplicate);
        var errors = CountFor(ResponseStatuses.Error);
        var actionReq = CountFor(ResponseStatuses.ActionRequired);
        var inProgress = CountFor(ResponseStatuses.InProgress);

        return (totalToday, duplicates, errors + actionReq, inProgress, actionReq);
    }

    private async Task<int> CountTodayWorkerEventErrorsAsync(
        HashSet<Guid> workerIds,
        DateTime todayStartUtc,
        CancellationToken ct)
    {
        if (workerIds.Count == 0)
        {
            return 0;
        }

        return await db.WorkerEvents
            .AsNoTracking()
            .Where(x => workerIds.Contains(x.WorkerId) && !x.IsDismissed && x.CreatedAtUtc >= todayStartUtc)
            .Where(x => x.Level == "Error" || x.Level == "Warning")
            .CountAsync(ct);
    }

    // Successful sends (CRM + Bitrix) in [utcStart, utcEnd) by actual send time.
    // One entry per successful delivery event; legacy rows that predate the delivery
    // journal are counted once by their process/send time (or CRM card creation time).
    private async Task<IReadOnlyList<DateTime>> LoadSendTimestampsAsync(
        HashSet<Guid> workerIds,
        DateTime utcStart,
        DateTime utcEnd,
        CancellationToken ct)
    {
        if (workerIds.Count == 0)
        {
            return [];
        }

        var scopedResponseIds = db.CandidateResponses.AsNoTracking()
            .Where(x => x.WorkerId != null && workerIds.Contains(x.WorkerId.Value))
            .Select(x => x.Id);

        var result = new List<DateTime>();

        var bitrixSends = await db.ResponseBitrixDeliveries.AsNoTracking()
            .Where(d => d.Outcome == ResponseBitrixDeliveryOutcomes.Sent
                && d.CreatedAtUtc >= utcStart
                && d.CreatedAtUtc < utcEnd
                && scopedResponseIds.Contains(d.ResponseId))
            .Select(d => d.CreatedAtUtc)
            .ToListAsync(ct);
        result.AddRange(bitrixSends);

        var crmSends = await db.ResponseCrmDeliveries.AsNoTracking()
            .Where(d => d.Outcome == ResponseCrmDeliveryOutcomes.Sent
                && d.CreatedAtUtc >= utcStart
                && d.CreatedAtUtc < utcEnd
                && scopedResponseIds.Contains(d.ResponseId))
            .Select(d => d.CreatedAtUtc)
            .ToListAsync(ct);
        result.AddRange(crmSends);

        // Legacy rows created before the delivery journal existed: Status=Sent responses
        // without any journal entry or CRM card are counted by their process/send time once.
        var responsesWithBitrixDelivery = db.ResponseBitrixDeliveries.AsNoTracking().Select(d => d.ResponseId);
        var responsesWithCrmDelivery = db.ResponseCrmDeliveries.AsNoTracking().Select(d => d.ResponseId);
        var responsesWithCrmCard = db.CrmCandidateCards.AsNoTracking().Select(c => c.ResponseId);
        var legacySends = await db.CandidateResponses.AsNoTracking()
            .Where(x => x.WorkerId != null
                && workerIds.Contains(x.WorkerId.Value)
                && x.Status == ResponseStatuses.Sent
                && x.ProcessedAt != null
                && x.ProcessedAt >= utcStart
                && x.ProcessedAt < utcEnd
                && !responsesWithBitrixDelivery.Contains(x.Id)
                && !responsesWithCrmDelivery.Contains(x.Id)
                && !responsesWithCrmCard.Contains(x.Id))
            .Select(x => x.ProcessedAt!.Value)
            .ToListAsync(ct);
        result.AddRange(legacySends);

        // Legacy CRM cards: card creation time is the CRM send time when no CRM delivery
        // journal entry exists for the same response and office.
        var crmDeliveryKeys = db.ResponseCrmDeliveries.AsNoTracking()
            .Select(d => new { d.ResponseId, d.OfficeId });
        var legacyCrmCards = await (
            from card in db.CrmCandidateCards.AsNoTracking()
            where card.CreatedAtUtc >= utcStart
                && card.CreatedAtUtc < utcEnd
                && !crmDeliveryKeys.Any(d => d.ResponseId == card.ResponseId && d.OfficeId == card.OfficeId)
            join response in db.CandidateResponses.AsNoTracking()
                on card.ResponseId equals response.Id
            where response.WorkerId != null && workerIds.Contains(response.WorkerId.Value)
            select card.CreatedAtUtc)
            .ToListAsync(ct);
        result.AddRange(legacyCrmCards);

        return result;
    }

    private async Task<WorkerEventErrorStats> ComputeWorkerEventErrorStatsAsync(
        HashSet<Guid> workerIds,
        DateTime todayStartUtc,
        DateTime eventsFromUtc,
        CancellationToken ct)
    {
        if (workerIds.Count == 0)
            return WorkerEventErrorStats.Empty;

        var rangeStart = eventsFromUtc < todayStartUtc ? eventsFromUtc : todayStartUtc;
        var rows = await db.WorkerEvents
            .AsNoTracking()
            .Where(x => workerIds.Contains(x.WorkerId) && !x.IsDismissed)
            .Where(x => x.CreatedAtUtc >= rangeStart)
            .Where(x => x.Level == "Error" || x.Level == "Warning")
            .Select(x => new { x.WorkerId, x.CreatedAtUtc })
            .ToListAsync(ct);

        return WorkerEventErrorStatsHelper.Compute(
            rows.Select(x => (x.WorkerId, x.CreatedAtUtc)),
            todayStartUtc);
    }

    private static Dictionary<Guid, (int Total, int Active)> BuildAccountCounts(
        IEnumerable<(Guid WorkerId, string Status, bool IsEnabledInPanel)> accountRows) =>
        accountRows
            .GroupBy(x => x.WorkerId)
            .ToDictionary(
                g => g.Key,
                g => (
                    Total: g.Count(),
                    Active: g.Count(a => AccountDashboardStatusClassifier.IsActiveInPanel(
                        a.Status,
                        a.IsEnabledInPanel))));

    private async Task<Dictionary<Guid, WorkerOperationalStats>> ComputeWorkerOperationalStatsAsync(
        IReadOnlyList<Guid> workerIds,
        DateTime todayStartUtc,
        CancellationToken ct)
    {
        if (workerIds.Count == 0)
            return [];

        var accountRows = await db.WorkerAccounts
            .AsNoTracking()
            .Where(x => workerIds.Contains(x.WorkerId))
            .Select(x => new
            {
                x.WorkerId,
                x.AccountId,
                x.Status,
                x.IsEnabledInPanel,
                x.TotalBalance,
                x.SubProfilesJson
            })
            .ToListAsync(ct);

        var accountCounts = BuildAccountCounts(
            accountRows.Select(x => (x.WorkerId, x.Status, x.IsEnabledInPanel)));
        var latestSnapshotBalances = await LoadLatestSnapshotBalancesAsync(workerIds, ct).ConfigureAwait(false);
        var lowBalanceAccountCounts = accountRows
            .Select(x =>
            {
                var snapshotBalance = latestSnapshotBalances.TryGetValue(x.WorkerId, out var balances)
                        && balances.TryGetValue(x.AccountId, out var liveSnapshotBalance)
                    ? liveSnapshotBalance
                    : null;
                return new
                {
                    x.WorkerId,
                    Count = ResolveLowBalanceSubProfileCount(
                        snapshotBalance,
                        x.TotalBalance,
                        x.SubProfilesJson)
                };
            })
            .GroupBy(x => x.WorkerId)
            .ToDictionary(g => g.Key, g => g.Sum(static x => x.Count));
        var responseStats = await ComputeWorkerTodayStatsAsync(workerIds, todayStartUtc, ct);
        var workerEventErrors = await ComputeWorkerEventErrorStatsAsync(
            workerIds.ToHashSet(),
            todayStartUtc,
            todayStartUtc,
            ct);

        return WorkerOperationalStatsHelper.Merge(
            workerIds,
            responseStats.ToDictionary(
                x => x.Key,
                x => (x.Value.Total, x.Value.Duplicates, x.Value.Errors)),
            workerEventErrors.PerWorkerToday,
            accountCounts,
            lowBalanceAccountCounts);
    }

    private static int ResolveLowBalanceSubProfileCount(
        WorkerBalanceDto? snapshotBalance,
        decimal persistedTotalBalance,
        string? persistedSubProfilesJson)
    {
        if (snapshotBalance is null)
        {
            return BalanceSnapshotHelper.CountLowBalancePersistedSubProfiles(
                persistedTotalBalance,
                persistedSubProfilesJson);
        }

        if (BalanceSnapshotHelper.HasKnownSubProfileAdvance(snapshotBalance))
        {
            return BalanceSnapshotHelper.CountLowBalanceSubProfiles(snapshotBalance);
        }

        var persistedCount = BalanceSnapshotHelper.CountLowBalancePersistedSubProfiles(
            persistedTotalBalance,
            persistedSubProfilesJson);
        return persistedCount > 0
            ? persistedCount
            : BalanceSnapshotHelper.CountLowBalanceSubProfiles(snapshotBalance);
    }

    private async Task<Dictionary<Guid, Dictionary<Guid, WorkerBalanceDto>>> LoadLatestSnapshotBalancesAsync(
        IReadOnlyList<Guid> workerIds,
        CancellationToken ct)
    {
        var snapshots = await WorkerSnapshotQuery.LoadLatestAsync(db, workerIds, ct)
            .ConfigureAwait(false);

        return snapshots.ToDictionary(
            x => x.WorkerId,
            x => (JsonSerializer.Deserialize<List<WorkerBalanceDto>>(x.BalancesJson, JsonOptions) ?? [])
                .GroupBy(balance => balance.AccountId)
                .ToDictionary(group => group.Key, group => group.Last()));
    }

    // Per-worker today response totals.
    private async Task<Dictionary<Guid, (int Total, int Duplicates, int Errors)>> ComputeWorkerTodayStatsAsync(
        IReadOnlyList<Guid> workerIds, DateTime todayStartUtc, CancellationToken ct)
    {
        var result = new Dictionary<Guid, (int Total, int Duplicates, int Errors)>();
        if (workerIds.Count == 0) return result;

        var idSet = workerIds.ToHashSet();
        var rows = await db.CandidateResponses.AsNoTracking()
            .Where(x => x.WorkerId != null && idSet.Contains(x.WorkerId.Value) && x.CollectedAt >= todayStartUtc)
            .GroupBy(x => x.WorkerId)
            .Select(g => new
            {
                WorkerId = g.Key,
                Total = g.Count(),
                Duplicates = g.Count(x => x.Status == ResponseStatuses.Duplicate),
                Errors = g.Count(x => x.Status == ResponseStatuses.Error || x.Status == ResponseStatuses.ActionRequired)
            })
            .ToListAsync(ct);

        foreach (var r in rows)
        {
            if (r.WorkerId is not Guid workerId)
            {
                continue;
            }

            result[workerId] = (Total: r.Total, Duplicates: r.Duplicates, Errors: r.Errors);
        }

        foreach (var wid in workerIds)
            if (!result.ContainsKey(wid)) result[wid] = (Total: 0, Duplicates: 0, Errors: 0);

        return result;
    }

    private static IReadOnlyList<WorkerSubProfileDto>? DeserializeSubProfiles(
        string? json,
        string? disabledIdsJson = null) =>
        SubProfileDeserializer.Deserialize(json, disabledIdsJson);

    private async Task<List<WorkerBalanceDto>> EnrichBalancesFromAccountsAsync(
        Guid workerId,
        IReadOnlyList<WorkerBalanceDto> balances,
        CancellationToken ct)
    {
        if (balances.Count == 0)
        {
            return [];
        }

        var existingAccounts = await db.WorkerAccounts
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId)
            .ToDictionaryAsync(x => x.AccountId, ct);

        return BalanceSnapshotHelper.MergeWithPersisted(
            balances,
            balances,
            existingAccounts).ToList();
    }

    private async Task<Dictionary<(Guid WorkerId, Guid AccountId), WorkerBalanceDto>> LoadLatestBalancesForWorkersAsync(
        IReadOnlyList<Guid> workerIds,
        CancellationToken ct)
    {
        var result = new Dictionary<(Guid WorkerId, Guid AccountId), WorkerBalanceDto>();
        if (workerIds.Count == 0)
        {
            return result;
        }

        var workerIdList = workerIds as List<Guid> ?? workerIds.ToList();
        var snapshots = await WorkerSnapshotQuery.LoadLatestAsync(db, workerIdList, ct);

        var existingAccounts = await db.WorkerAccounts
            .AsNoTracking()
            .Where(x => workerIdList.Contains(x.WorkerId))
            .ToListAsync(ct);

        var accountsByWorker = existingAccounts
            .GroupBy(x => x.WorkerId)
            .ToDictionary(
                static g => g.Key,
                static g => (IReadOnlyDictionary<Guid, WorkerAccountEntity>)g.ToDictionary(a => a.AccountId));

        foreach (var snapshot in snapshots)
        {
            var balances = JsonSerializer.Deserialize<List<WorkerBalanceDto>>(snapshot.BalancesJson, JsonOptions)
                ?? [];
            if (balances.Count == 0)
            {
                continue;
            }

            accountsByWorker.TryGetValue(snapshot.WorkerId, out var workerAccounts);
            workerAccounts ??= new Dictionary<Guid, WorkerAccountEntity>();
            foreach (var balance in BalanceSnapshotHelper.MergeWithPersisted(balances, balances, workerAccounts))
            {
                result[(snapshot.WorkerId, balance.AccountId)] = balance;
            }
        }

        foreach (var account in existingAccounts)
        {
            if (result.ContainsKey((account.WorkerId, account.AccountId)))
            {
                continue;
            }

            var fromAccount = BalanceSnapshotHelper.FromWorkerAccount(account);
            if (fromAccount is not null)
            {
                result[(account.WorkerId, account.AccountId)] = fromAccount;
            }
        }

        return result;
    }

    private async Task TryBackfillSubProfilesFromResponsesAsync(
        Guid workerId,
        IReadOnlyList<Guid> emptyAccountIds,
        IReadOnlyList<WorkerAccountDto> accountDtos,
        CancellationToken ct)
    {
        if (emptyAccountIds.Count == 0)
        {
            return;
        }

        var backfillByAccount = accountDtos
            .Where(x => emptyAccountIds.Contains(x.AccountId) && x.SubProfiles is { Count: > 0 })
            .ToDictionary(x => x.AccountId, x => x.SubProfiles!);
        if (backfillByAccount.Count == 0)
        {
            return;
        }

        var accounts = await db.WorkerAccounts
            .Where(x => x.WorkerId == workerId
                && backfillByAccount.Keys.Contains(x.AccountId)
                && (x.SubProfilesJson == "[]" || x.SubProfilesJson == ""))
            .ToListAsync(ct);

        if (accounts.Count == 0)
        {
            return;
        }

        foreach (var account in accounts)
        {
            if (!backfillByAccount.TryGetValue(account.AccountId, out var profiles))
            {
                continue;
            }

            account.SubProfilesJson = JsonSerializer.Serialize(profiles, SubProfileJsonOptions.Serialize);
        }

        await db.SaveChangesAsync(ct);
    }
}

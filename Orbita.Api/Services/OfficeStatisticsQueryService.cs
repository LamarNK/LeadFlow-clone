using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class OfficeStatisticsQueryService(
    OrbitaDbContext db,
    OfficeScopeService officeScope,
    WorkerConnectionRegistry connectionRegistry)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static (
        DateTime ExpiresUtc,
        OfficeStatisticsDto? Value,
        OfficeScope Scope,
        Guid? OfficeFilter,
        DateTime FromLocal,
        DateTime ToLocal,
        string WorkerFilterKey,
        string AccountFilterKey,
        string VacancyFilterKey) _cache;
    private static readonly object CacheLock = new();

    public async Task<OfficeStatisticsDto> GetStatisticsAsync(
        OfficeScope scope,
        Guid? officeFilter,
        DateTime? from,
        DateTime? to,
        IReadOnlyList<Guid>? workerIdsFilter = null,
        IReadOnlyList<Guid>? accountIdsFilter = null,
        string? vacancyFilter = null,
        CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var (startLocal, endLocal, utcStart, utcEnd) = LocalCalendarDateRange.Normalize(from, to);
        var workerFilterSet = NormalizeFilter(workerIdsFilter);
        var accountFilterSet = NormalizeFilter(accountIdsFilter);
        var workerFilterKey = BuildFilterKey(workerFilterSet);
        var accountFilterKey = BuildFilterKey(accountFilterSet);
        var vacancyFilterKey = string.IsNullOrWhiteSpace(vacancyFilter) ? string.Empty : vacancyFilter.Trim();

        OfficeStatisticsDto? cachedResult = null;
        lock (CacheLock)
        {
            if (_cache.Value is not null
                && _cache.ExpiresUtc > nowUtc
                && _cache.Scope.IsGlobalAdmin == scope.IsGlobalAdmin
                && _cache.OfficeFilter == officeFilter
                && _cache.FromLocal == startLocal
                && _cache.ToLocal == endLocal
                && _cache.WorkerFilterKey == workerFilterKey
                && _cache.AccountFilterKey == accountFilterKey
                && _cache.VacancyFilterKey == vacancyFilterKey)
            {
                cachedResult = _cache.Value;
            }
        }

        if (cachedResult is not null)
        {
            return await RefreshOnlineStatusAsync(cachedResult, ct);
        }

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
                a.SubProfilesJson))
            .ToListAsync(ct);

        if (accountFilterSet is not null)
        {
            accountRows = accountRows.Where(a => accountFilterSet.Contains(a.AccountId)).ToList();
        }

        var workerLookup = workers.ToDictionary(w => w.Id);
        var balances = BuildBalances(accountRows, snapshotBalancesByWorker, workerLookup);
        var accountInfrastructure = BuildAccountInfrastructure(accountRows, statsList);
        var responsesQuery = db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.WorkerId != null && workerIds.Contains(x.WorkerId.Value) && x.CollectedAt >= utcStart && x.CollectedAt < utcEnd);

        if (accountFilterSet is not null)
        {
            responsesQuery = responsesQuery.Where(x => accountFilterSet.Contains(x.AccountId));
        }

        responsesQuery = ApplyVacancyFilter(responsesQuery, vacancyFilterKey);

        var dailyTrend = await BuildDailyTrendAsync(responsesQuery, startLocal, endLocal, ct);
        var responses = await BuildResponsesPeriodAsync(responsesQuery, dailyTrend, ct);
        var bitrixDeliveries = await BuildBitrixDeliveryStatsAsync(responsesQuery, scope, officeFilter, ct);
        var hrInsights = await BuildHrInsightsAsync(responsesQuery, ct);
        var workerInfrastructure = await BuildWorkerInfrastructureAsync(
            workers,
            workerIds,
            utcStart,
            utcEnd,
            accountRows,
            ct);
        var monitoringCycles = await BuildMonitoringCyclesAsync(
            workerIds,
            utcStart,
            utcEnd,
            startLocal,
            endLocal,
            accountRows,
            ct);

        var result = new OfficeStatisticsDto(
            balances,
            accountInfrastructure,
            workerInfrastructure,
            responses,
            dailyTrend,
            bitrixDeliveries,
            hrInsights,
            monitoringCycles,
            nowUtc);

        lock (CacheLock)
        {
            _cache = (nowUtc.AddSeconds(8), result, scope, officeFilter, startLocal, endLocal, workerFilterKey, accountFilterKey, vacancyFilterKey);
        }

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

        var logRows = await db.WorkerLogEntries
            .AsNoTracking()
            .Where(x => workerIds.Contains(x.WorkerId))
            .Where(x => x.TimestampUtc >= utcStart && x.TimestampUtc < utcEnd)
            .Where(x =>
                EF.Functions.ILike(x.Message, "%переключаем суб-профиль%")
                || EF.Functions.ILike(x.Message, "%переключение субпрофиля%")
                || EF.Functions.ILike(x.Message, "%новых для LeadFlow%")
                || EF.Functions.ILike(x.Message, "%обработано сейчас%")
                || EF.Functions.ILike(x.Message, "%новых к публикации%")
                || EF.Functions.ILike(x.Message, "%— отклики:%")
                || EF.Functions.ILike(x.Message, "%— опубликовано%")
                || (EF.Functions.ILike(x.Message, "%(объявления)%") && EF.Functions.ILike(x.Message, "%активных%"))
                || EF.Functions.ILike(x.Message, "%Не удалось обработать суб-профиль%")
                || EF.Functions.ILike(x.Message, "%— проблема (%")
                || EF.Functions.ILike(x.Message, "%— не переключился:%"))
            .OrderBy(x => x.TimestampUtc)
            .Select(x => new { x.TimestampUtc, x.Message })
            .ToListAsync(ct);

        var rows = logRows
            .Select(x => (x.TimestampUtc, x.Message, PropertiesJson: (string?)null))
            .ToList();

        var sentResponses = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.WorkerId != null && workerIds.Contains(x.WorkerId.Value))
            .Where(x => x.Status == ResponseStatuses.Sent)
            .Where(x => x.CollectedAt >= utcStart && x.CollectedAt < utcEnd)
            .Select(x => new
            {
                x.AccountName,
                x.AvitoSubProfileName,
                TimestampUtc = x.ProcessedAt ?? x.CollectedAt
            })
            .ToListAsync(ct);

        var sentRows = sentResponses
            .Select(x => new MonitoringCycleSentResponse(
                x.AccountName.Trim(),
                x.AvitoSubProfileName.Trim(),
                x.TimestampUtc))
            .Where(x => allowedAccountNames.Contains(x.AccountName))
            .ToList();

        return MonitoringCycleReportBuilder.Build(rows, startLocal, endLocal, allowedAccountNames, sentRows);
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
        IQueryable<CandidateResponseEntity> query,
        IReadOnlyList<DailyResponseBucketDto> dailyTrend,
        CancellationToken ct)
    {
        var total = dailyTrend.Sum(x => x.Total);
        if (total == 0)
        {
            return new ResponsesPeriodSection(0, 0, 0, 0, 0, 0, 0, 0, null);
        }

        var sent = dailyTrend.Sum(x => x.Sent);
        var duplicates = dailyTrend.Sum(x => x.Duplicates);
        var errors = dailyTrend.Sum(x => x.Errors);
        var actionRequired = dailyTrend.Sum(x => x.ActionRequired);
        var inProgress = dailyTrend.Sum(x => x.InProgress);
        var unique = total - duplicates;
        var uniqueAuthors = await ResponseSummaryMetrics.CountUniqueAuthorsAsync(query, ct);

        var avgMinutes = await query
            .Where(x => x.ProcessedAt != null)
            .AverageAsync(x => (double?)(x.ProcessedAt!.Value - x.CollectedAt).TotalMinutes, ct);

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
        IQueryable<CandidateResponseEntity> responsesQuery,
        OfficeScope scope,
        Guid? officeFilter,
        CancellationToken ct)
    {
        var deliveryCounts = await db.ResponseBitrixDeliveries
            .AsNoTracking()
            .Where(d => d.Outcome == ResponseBitrixDeliveryOutcomes.Sent)
            .Where(d => responsesQuery.Any(r => r.Id == d.ResponseId))
            .GroupBy(d => d.BitrixInstanceId)
            .Select(g => new { BitrixInstanceId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var legacyCounts = await responsesQuery
            .Where(x => x.BitrixInstanceId != null && x.Status == ResponseStatuses.Sent)
            .Where(x => !db.ResponseBitrixDeliveries.Any(d => d.ResponseId == x.Id))
            .GroupBy(x => x.BitrixInstanceId!.Value)
            .Select(g => new { BitrixInstanceId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var merged = new Dictionary<Guid, int>();
        foreach (var row in deliveryCounts)
        {
            merged[row.BitrixInstanceId] = row.Count;
        }

        foreach (var row in legacyCounts)
        {
            merged[row.BitrixInstanceId] = merged.GetValueOrDefault(row.BitrixInstanceId) + row.Count;
        }

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

    private static async Task<IReadOnlyList<DailyResponseBucketDto>> BuildDailyTrendAsync(
        IQueryable<CandidateResponseEntity> query,
        DateTime startLocal,
        DateTime endLocal,
        CancellationToken ct)
    {
        var rows = await query
            .GroupBy(x => new { Date = x.CollectedAt.Date, x.CollectedAt.Hour, x.Status })
            .Select(g => new HourlyStatusCount(g.Key.Date, g.Key.Hour, g.Key.Status, g.Count()))
            .ToListAsync(ct);

        var byDay = new Dictionary<DateTime, DailyCounters>();
        foreach (var row in rows)
        {
            var utcHour = DateTime.SpecifyKind(row.Date.AddHours(row.Hour), DateTimeKind.Utc);
            var localDate = LocalCalendarDateRange.ToLocalDateFromStoredUtc(utcHour);
            if (!byDay.TryGetValue(localDate, out var bucket))
            {
                bucket = new DailyCounters();
                byDay[localDate] = bucket;
            }

            bucket.Total += row.Count;
            switch (row.Status)
            {
                case ResponseStatuses.Sent:
                    bucket.Sent += row.Count;
                    break;
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

        var responseStats = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.WorkerId != null && workerIds.Contains(x.WorkerId.Value) && x.CollectedAt >= utcStart && x.CollectedAt < utcEnd)
            .GroupBy(x => x.WorkerId)
            .Select(g => new
            {
                WorkerId = g.Key,
                Total = g.Count(),
                Sent = g.Count(x => x.Status == ResponseStatuses.Sent),
                Duplicates = g.Count(x => x.Status == ResponseStatuses.Duplicate),
                Errors = g.Count(x => x.Status == ResponseStatuses.Error || x.Status == ResponseStatuses.ActionRequired)
            })
            .ToListAsync(ct);

        var responseLookup = responseStats
            .Where(x => x.WorkerId is not null)
            .ToDictionary(x => x.WorkerId!.Value);

        var items = workers
            .Select(w =>
            {
                responseLookup.TryGetValue(w.Id, out var stats);
                accountCounts.TryGetValue(w.Id, out var accounts);
                return new WorkerStatisticsRowDto(
                    w.Id,
                    w.DisplayName,
                    w.OfficeName,
                    WorkerOnlineRules.IsOnline(w.LastSeenAtUtc, nowUtc, connectionRegistry.IsConnected(w.Id)),
                    stats?.Total ?? 0,
                    stats?.Sent ?? 0,
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
                    p.AdvanceDurationText))
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

    private sealed record AccountProjection(
        Guid WorkerId,
        Guid AccountId,
        string DisplayName,
        string Status,
        bool IsEnabledInPanel,
        decimal TotalBalance,
        string SubProfilesJson);

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
}

using System.Globalization;
using LeadFlow;
using LeadFlow.Models;
using LeadFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace LeadFlow.Data;

public sealed class AppRepository(IDbContextFactory<AppDbContext> dbContextFactory) : ICandidateDuplicateRepository, IMonitoringRepository
{
    /// <summary>Срабатывает после успешного сохранения аккаунта в БД. Подписчики не должны изменять переданный экземпляр.</summary>
    public event EventHandler<AvitoAccount>? AccountPersisted;
    public async Task InitializeAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureAvitoAccountsSchemaAsync(db, cancellationToken);
        await EnsureCandidateResponsesSchemaAsync(db, cancellationToken);

        foreach (var account in settings.Avito.Accounts)
        {
            if (await db.AvitoAccounts.FindAsync([account.Id], cancellationToken) is null)
            {
                db.AvitoAccounts.Add(ToEntity(account));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAccountAsync(AvitoAccount account, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.AvitoAccounts.FindAsync([account.Id], cancellationToken);
        if (existing is null)
        {
            db.AvitoAccounts.Add(ToEntity(account));
        }
        else
        {
            Map(account, existing);
        }

        await db.SaveChangesAsync(cancellationToken);
        AccountPersisted?.Invoke(this, account);
    }

    public async Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.AvitoAccounts.FindAsync([accountId], cancellationToken);
        if (existing is not null)
        {
            db.AvitoAccounts.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AvitoAccount>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AvitoAccounts
            .AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .Select(x => ToModel(x))
            .ToListAsync(cancellationToken);
    }

    public async Task<AvitoAccount?> GetAccountByIdAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.AvitoAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        return entity is null ? null : ToModel(entity);
    }

    public async Task SaveCandidateAsync(CandidateResponse response, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.CandidateResponses.FindAsync([response.Id], cancellationToken);
        if (existing is null)
        {
            if (!string.IsNullOrWhiteSpace(response.SourceResponseId))
            {
                var sameSource = await db.CandidateResponses
                    .FirstOrDefaultAsync(
                        x => x.AccountId == response.AccountId && x.SourceResponseId == response.SourceResponseId,
                        cancellationToken);
                if (sameSource is not null)
                {
                    response.Id = sameSource.Id;
                    Map(response, sameSource);
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }
            }

            db.CandidateResponses.Add(ToEntity(response));
        }
        else
        {
            Map(response, existing);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteCandidateResponseAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var logs = await db.ProcessingLogs.Where(x => x.CandidateResponseId == id).ToListAsync(cancellationToken);
        if (logs.Count > 0)
        {
            db.ProcessingLogs.RemoveRange(logs);
        }

        var existing = await db.CandidateResponses.FindAsync([id], cancellationToken);
        if (existing is not null)
        {
            db.CandidateResponses.Remove(existing);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddLogAsync(ProcessingLogItem item, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.ProcessingLogs.Add(new ProcessingLogEntity
        {
            Id = item.Id,
            CandidateResponseId = item.CandidateResponseId,
            AccountId = item.AccountId,
            CreatedAt = item.CreatedAt,
            Level = item.Level,
            Message = item.Message,
            Details = item.Details
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CandidateResponse>> GetRecentResponsesAsync(int take, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.CandidateResponses
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .Select(x => ToModel(x))
            .ToListAsync(cancellationToken);
    }

    public async Task<HashSet<string>> GetExistingSourceResponseIdsAsync(IEnumerable<string> sourceResponseIds, CancellationToken cancellationToken)
    {
        var normalizedIds = sourceResponseIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedIds.Length == 0)
        {
            return [];
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.CandidateResponses
            .Where(x => normalizedIds.Contains(x.SourceResponseId))
            .Select(x => x.SourceResponseId)
            .ToListAsync(cancellationToken);

        return existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<HashSet<string>> GetExistingBitrixEntityIdsAsync(IEnumerable<string> bitrixEntityIds, CancellationToken cancellationToken)
    {
        var normalizedIds = bitrixEntityIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedIds.Length == 0)
        {
            return [];
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.CandidateResponses
            .Where(x => normalizedIds.Contains(x.BitrixEntityId))
            .Select(x => x.BitrixEntityId)
            .ToListAsync(cancellationToken);

        return existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<double> GetHistoricalResponseIngestHeatScoreAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var cut = utcNow.AddDays(-MonitoringTiming.CycleHistoricalHeatLookbackDays);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var timestamps = await db.CandidateResponses
            .AsNoTracking()
            .Where(c => c.CreatedAt >= cut)
            .Select(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        return MonitoringHistoricalHeat.ComputeScore(timestamps, utcNow, TimeZoneInfo.Local);
    }

    public async Task<IReadOnlyList<ProcessingLogItem>> GetRecentLogsAsync(int take, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ProcessingLogs
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .Select(x => new ProcessingLogItem
            {
                Id = x.Id,
                CandidateResponseId = x.CandidateResponseId,
                AccountId = x.AccountId,
                CreatedAt = x.CreatedAt,
                Level = x.Level,
                Message = x.Message,
                Details = x.Details
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<DashboardStats> GetDashboardStatsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var (utcStart, utcEnd) = DateTimeAssumedUtc.GetUtcRangeForLocalToday();
        var statusCounts = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.CreatedAt >= utcStart && x.CreatedAt < utcEnd)
            .GroupBy(x => x.Status)
            .Select(g => new StatusCountRow(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var totalResponses = statusCounts.Sum(x => x.Count);
        var newResponses = statusCounts.Where(x => x.Status == nameof(ResponseStatus.New)).Sum(x => x.Count);
        var sentResponses = statusCounts.Where(x => x.Status == nameof(ResponseStatus.Sent)).Sum(x => x.Count);
        var inProgressResponses = statusCounts.Where(x => x.Status == nameof(ResponseStatus.InProgress)).Sum(x => x.Count);
        var duplicateResponses = statusCounts.Where(x => x.Status == nameof(ResponseStatus.Duplicate)).Sum(x => x.Count);
        var errorResponses = statusCounts.Where(x => x.Status == nameof(ResponseStatus.Error)).Sum(x => x.Count);
        var actionRequiredResponses = statusCounts.Where(x => x.Status == nameof(ResponseStatus.ActionRequired)).Sum(x => x.Count);

        var hourlyGroupsUtc = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.CreatedAt >= utcStart && x.CreatedAt < utcEnd)
            .GroupBy(x => new { x.CreatedAt.Year, x.CreatedAt.Month, x.CreatedAt.Day, x.CreatedAt.Hour })
            .Select(g => new HourlyStatusAggregateRow(
                g.Key.Year,
                g.Key.Month,
                g.Key.Day,
                g.Key.Hour,
                g.Count(),
                g.Count(x => x.Status == nameof(ResponseStatus.Sent)),
                g.Count(x => x.Status == nameof(ResponseStatus.Duplicate)),
                g.Count(x => x.Status == nameof(ResponseStatus.Error))))
            .ToListAsync(cancellationToken);

        var byHour = new Dictionary<int, HourlyCounters>();
        foreach (var row in hourlyGroupsUtc)
        {
            var utcHour = new DateTime(row.Year, row.Month, row.Day, row.Hour, 0, 0, DateTimeKind.Utc);
            var localHour = utcHour.ToLocalTimeFromStoredUtc().Hour;
            if (!byHour.TryGetValue(localHour, out var bucket))
            {
                bucket = new HourlyCounters();
                byHour[localHour] = bucket;
            }

            bucket.Total += row.Total;
            bucket.Sent += row.Sent;
            bucket.Duplicates += row.Duplicates;
            bucket.Errors += row.Errors;
        }

        var accountSummary = await db.AvitoAccounts
            .AsNoTracking()
            .GroupBy(static _ => 1)
            .Select(g => new
            {
                Connected = g.Count(x => x.IsEnabled),
                RequiresAuthorization = g.Count(x => x.Status == nameof(AvitoAccountStatus.RequiresLogin)),
                AccountsNeedAttention = g.Count(x => x.IsEnabled
                    && (x.Status == nameof(AvitoAccountStatus.RequiresLogin)
                        || x.Status == nameof(AvitoAccountStatus.RequiresManualAction)
                        || x.Status == nameof(AvitoAccountStatus.Error))),
                ActiveAds = g.Sum(x => x.ActiveAdsCount),
                BlockedAds = g.Sum(x => x.BlockedCount),
                Drafts = g.Sum(x => x.DraftsCount)
            })
            .FirstOrDefaultAsync(cancellationToken);

        var totalToday = totalResponses;
        var stats = new DashboardStats
        {
            NewResponses = newResponses,
            TotalToday = totalToday,
            SentToCrm = sentResponses,
            InProgress = inProgressResponses,
            Duplicates = duplicateResponses,
            Errors = errorResponses,
            ActionRequired = actionRequiredResponses,
            ConnectedAccounts = accountSummary?.Connected ?? 0,
            RequiresAuthorization = accountSummary?.RequiresAuthorization ?? 0,
            AccountsNeedAttentionCount = accountSummary?.AccountsNeedAttention ?? 0,
            ActiveAdsCount = accountSummary?.ActiveAds ?? 0,
            BlockedAdsCount = accountSummary?.BlockedAds ?? 0,
            DraftsCount = accountSummary?.Drafts ?? 0
        };

        for (var hour = 0; hour < 24; hour++)
        {
            _ = byHour.TryGetValue(hour, out var bucket);
            stats.HourlyActivity.Add(new ActivityPoint
            {
                Label = $"{hour:00}:00",
                SlotStartHour = hour,
                SlotSpanHours = 1,
                NewCount = bucket?.Total ?? 0,
                SentCount = bucket?.Sent ?? 0,
                DuplicateCount = bucket?.Duplicates ?? 0,
                ErrorCount = bucket?.Errors ?? 0
            });
        }

        var weekStartLocal = DateTime.Today.AddDays(-6);
        var weekDays = await GetDailyResponseStatsForLocalRangeAsync(weekStartLocal, DateTime.Today, cancellationToken);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        foreach (var day in weekDays)
        {
            stats.WeeklyByDayActivity.Add(new ActivityPoint
            {
                Label = day.DateLocal.ToString("ddd d.MM", ru),
                LocalDate = day.DateLocal,
                NewCount = day.Total,
                SentCount = day.Sent,
                DuplicateCount = day.Duplicates,
                ErrorCount = day.Errors,
                SlotStartHour = 0,
                SlotSpanHours = 1
            });
        }

        stats.AggregatedUpToUtc = DateTime.UtcNow;
        return stats;
    }

    /// <summary>Отклики по календарным дням (локально), за последние <paramref name="dayCount"/> дней включая сегодня.</summary>
    public Task<IReadOnlyList<DailyResponseBucket>> GetDailyResponseStatsAsync(int dayCount, CancellationToken cancellationToken)
    {
        dayCount = Math.Clamp(dayCount, 1, 366);
        var todayLocal = DateTime.Today;
        var firstDayLocal = todayLocal.AddDays(-(dayCount - 1));
        return GetDailyResponseStatsForLocalRangeAsync(firstDayLocal, todayLocal, cancellationToken);
    }

    /// <summary>
    /// Отклики по календарным дням (локально) на интервале [<paramref name="startLocalDate"/>, <paramref name="endLocalDate"/>] включительно.
    /// Конец не позже сегодня; при перепутанных датах границы меняются местами; не более 366 дней.
    /// </summary>
    public async Task<IReadOnlyList<DailyResponseBucket>> GetDailyResponseStatsForLocalRangeAsync(
        DateTime startLocalDate,
        DateTime endLocalDate,
        CancellationToken cancellationToken)
    {
        const int maxCalendarDays = 366;
        var todayLocal = DateTime.Today;
        var start = startLocalDate.Date;
        var end = endLocalDate.Date;

        if (end > todayLocal)
        {
            end = todayLocal;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        if ((end - start).Days + 1 > maxCalendarDays)
        {
            start = end.AddDays(-(maxCalendarDays - 1));
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var utcStart = DateTimeAssumedUtc.GetUtcRangeForLocalCalendarDay(start).UtcStartInclusive;
        var utcEnd = DateTimeAssumedUtc.GetUtcRangeForLocalCalendarDay(end).UtcEndExclusive;

        var groupedByUtcHour = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.CreatedAt >= utcStart && x.CreatedAt < utcEnd)
            .GroupBy(x => new { x.CreatedAt.Year, x.CreatedAt.Month, x.CreatedAt.Day, x.CreatedAt.Hour })
            .Select(g => new HourlyStatusAggregateRow(
                g.Key.Year,
                g.Key.Month,
                g.Key.Day,
                g.Key.Hour,
                g.Count(),
                g.Count(x => x.Status == nameof(ResponseStatus.Sent)),
                g.Count(x => x.Status == nameof(ResponseStatus.Duplicate)),
                g.Count(x => x.Status == nameof(ResponseStatus.Error)),
                g.Count(x => x.Status == nameof(ResponseStatus.InProgress)),
                g.Count(x => x.Status == nameof(ResponseStatus.ActionRequired))))
            .ToListAsync(cancellationToken);

        var byDay = new Dictionary<DateTime, DailyCounters>();
        foreach (var row in groupedByUtcHour)
        {
            var utcHour = new DateTime(row.Year, row.Month, row.Day, row.Hour, 0, 0, DateTimeKind.Utc);
            var localDate = utcHour.ToLocalTimeFromStoredUtc().Date;
            if (!byDay.TryGetValue(localDate, out var bucket))
            {
                bucket = new DailyCounters();
                byDay[localDate] = bucket;
            }

            bucket.Total += row.Total;
            bucket.Sent += row.Sent;
            bucket.InProgress += row.InProgress;
            bucket.ActionRequired += row.ActionRequired;
            bucket.Duplicates += row.Duplicates;
            bucket.Errors += row.Errors;
        }

        var capacity = (end - start).Days + 1;
        var list = new List<DailyResponseBucket>(capacity);
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (!byDay.TryGetValue(d, out var dayItems))
            {
                list.Add(new DailyResponseBucket
                {
                    DateLocal = d,
                    Total = 0,
                    Sent = 0,
                    InProgress = 0,
                    ActionRequired = 0,
                    Duplicates = 0,
                    Errors = 0
                });
                continue;
            }

            list.Add(new DailyResponseBucket
            {
                DateLocal = d,
                Total = dayItems.Total,
                Sent = dayItems.Sent,
                InProgress = dayItems.InProgress,
                ActionRequired = dayItems.ActionRequired,
                Duplicates = dayItems.Duplicates,
                Errors = dayItems.Errors
            });
        }

        return list;
    }

    /// <summary>HR-разрезы за локальный календарный период: города, вакансии, аккаунты, возраст и покрытие мессенджером.</summary>
    public async Task<HrInsightsSnapshot> GetHrInsightsForLocalRangeAsync(
        DateTime startLocalDate,
        DateTime endLocalDate,
        CancellationToken cancellationToken)
    {
        var todayLocal = DateTime.Today;
        var start = startLocalDate.Date;
        var end = endLocalDate.Date;
        if (end > todayLocal)
        {
            end = todayLocal;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        const int maxCalendarDays = 366;
        if ((end - start).Days + 1 > maxCalendarDays)
        {
            start = end.AddDays(-(maxCalendarDays - 1));
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var utcStart = DateTimeAssumedUtc.GetUtcRangeForLocalCalendarDay(start).UtcStartInclusive;
        var utcEnd = DateTimeAssumedUtc.GetUtcRangeForLocalCalendarDay(end).UtcEndExclusive;

        var baseQuery = db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.CreatedAt >= utcStart && x.CreatedAt < utcEnd);

        var total = await baseQuery.CountAsync(cancellationToken);
        if (total == 0)
        {
            return new HrInsightsSnapshot();
        }

        var withMessenger = await baseQuery.CountAsync(x => x.MessengerUrl != null && x.MessengerUrl != string.Empty, cancellationToken);
        var avgAge = await baseQuery
            .Where(x => x.Age.HasValue)
            .Select(x => (double?)x.Age)
            .AverageAsync(cancellationToken);

        var cityRows = await baseQuery
            .GroupBy(x => new { x.City, x.Status })
            .Select(g => new GroupedStatusRow(g.Key.City, g.Key.Status, g.Count()))
            .ToListAsync(cancellationToken);
        var vacancyRows = await baseQuery
            .GroupBy(x => new { x.Vacancy, x.Status })
            .Select(g => new GroupedStatusRow(g.Key.Vacancy, g.Key.Status, g.Count()))
            .ToListAsync(cancellationToken);
        var accountRows = await baseQuery
            .GroupBy(x => new { x.AccountName, x.Status })
            .Select(g => new GroupedStatusRow(g.Key.AccountName, g.Key.Status, g.Count()))
            .ToListAsync(cancellationToken);
        var ageRows = await baseQuery
            .GroupBy(x => new { x.Age, x.Status })
            .Select(g => new GroupedAgeStatusRow(g.Key.Age, g.Key.Status, g.Count()))
            .ToListAsync(cancellationToken);

        string Percent(int part, int whole) => whole <= 0 ? "0%" : $"{Math.Round(part * 100d / whole, 1):0.#}%";
        static bool IsSent(string status) => status == nameof(ResponseStatus.Sent);
        static string Normalize(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        IReadOnlyList<HrMetricRow> BuildTop(
            IEnumerable<GroupedStatusRow> groupedRows,
            string fallback,
            int take) =>
            groupedRows
                .GroupBy(x => Normalize(x.Key, fallback))
                .Select(g =>
                {
                    var sent = g.Where(x => IsSent(x.Status)).Sum(x => x.Count);
                    var count = g.Sum(x => x.Count);
                    return new HrMetricRow
                    {
                        Name = g.Key,
                        Total = count,
                        Sent = sent,
                        ConversionText = Percent(sent, count),
                        ShareText = Percent(count, total)
                    };
                })
                .OrderByDescending(x => x.Total)
                .ThenBy(x => x.Name)
                .Take(take)
                .ToList();

        var topCities = BuildTop(
            cityRows,
            "Город не указан",
            8);
        var topVacancies = BuildTop(
            vacancyRows,
            "Вакансия не указана",
            8);
        var topAccounts = BuildTop(
            accountRows,
            "Аккаунт не указан",
            8);

        string GetAgeBucket(int? age) => age switch
        {
            null => "Возраст не указан",
            < 18 => "< 18",
            <= 24 => "18-24",
            <= 34 => "25-34",
            <= 44 => "35-44",
            _ => "45+"
        };

        var ageBuckets = ageRows
            .GroupBy(x => GetAgeBucket(x.Age))
            .Select(g =>
            {
                var count = g.Sum(x => x.Count);
                var sent = g.Where(x => IsSent(x.Status)).Sum(x => x.Count);
                return new AgeBucketMetricRow
                {
                    Bucket = g.Key,
                    Total = count,
                    Sent = sent,
                    ConversionText = Percent(sent, count)
                };
            })
            .OrderByDescending(x => x.Total)
            .ToList();

        var avgAgeText = avgAge is null ? "н/д" : $"{Math.Round(avgAge.Value, 1):0.#} лет";

        return new HrInsightsSnapshot
        {
            TopCities = topCities,
            TopVacancies = topVacancies,
            TopAccounts = topAccounts,
            AgeBuckets = ageBuckets,
            AverageAgeText = avgAgeText,
            MessengerCoverageText = Percent(withMessenger, total)
        };
    }

    private sealed record StatusCountRow(string Status, int Count);
    private sealed record GroupedStatusRow(string? Key, string Status, int Count);
    private sealed record GroupedAgeStatusRow(int? Age, string Status, int Count);
    private sealed record HourlyStatusAggregateRow(
        int Year,
        int Month,
        int Day,
        int Hour,
        int Total,
        int Sent,
        int Duplicates,
        int Errors,
        int InProgress = 0,
        int ActionRequired = 0);
    private sealed class HourlyCounters
    {
        public int Total { get; set; }
        public int Sent { get; set; }
        public int Duplicates { get; set; }
        public int Errors { get; set; }
    }
    private sealed class DailyCounters
    {
        public int Total { get; set; }
        public int Sent { get; set; }
        public int InProgress { get; set; }
        public int ActionRequired { get; set; }
        public int Duplicates { get; set; }
        public int Errors { get; set; }
    }

    public async Task<CandidateResponse?> FindDuplicateAsync(string phoneNormalized, DuplicateScope scope, Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<CandidateResponseEntity> query = db.CandidateResponses.Where(x => x.PhoneNormalized == phoneNormalized);
        if (scope == DuplicateScope.PerAvitoAccount)
        {
            query = query.Where(x => x.AccountId == accountId);
        }

        var entity = await query.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToModel(entity);
    }

    private static AvitoAccountEntity ToEntity(AvitoAccount model) => new()
    {
        Id = model.Id,
        DisplayName = model.DisplayName,
        AvitoResponsesUrl = model.AvitoResponsesUrl,
        BrowserProfilePath = model.BrowserProfilePath,
        IsEnabled = model.IsEnabled,
        Status = model.Status.ToString(),
        LastAuthCheckAt = model.LastAuthCheckAt,
        LastMonitoringAt = model.LastMonitoringAt,
        LastErrorMessage = model.LastErrorMessage,
        BrowserName = model.BrowserName,
        BrowserVersion = model.BrowserVersion,
        UserAgentDevice = model.UserAgentDevice,
        UseWindowsOs = model.UseWindowsOs,
        WindowsVersion = model.WindowsVersion,
        UseMacOs = model.UseMacOs,
        MacOsVersion = model.MacOsVersion,
        UseLinuxOs = model.UseLinuxOs,
        LinuxVersion = model.LinuxVersion,
        UseAndroidOs = model.UseAndroidOs,
        AndroidVersion = model.AndroidVersion,
        UseIosOs = model.UseIosOs,
        IosVersion = model.IosVersion,
        AssignedUserAgent = model.AssignedUserAgent,
        CookiesJson = model.CookiesJson,
        ImportCookiesOnNextStart = model.ImportCookiesOnNextStart,
        Notes = model.Notes,
        ProxyAddress = model.ProxyAddress,
        ProxyType = string.IsNullOrWhiteSpace(model.ProxyType) ? "http" : model.ProxyType,
        ProxyUsername = model.ProxyUsername,
        ProxyPassword = model.ProxyPassword,
        ProxyRotationUrl = model.ProxyRotationUrl,
        BrowserLaunchArgs = model.BrowserLaunchArgs ?? string.Empty,
        NavigatorPlatform = model.NavigatorPlatform,
        DoNotTrack = model.DoNotTrack,
        WebGlVendor = model.WebGlVendor,
        WebGlRenderer = model.WebGlRenderer,
        SpoofWebGl = model.SpoofWebGl,
        CanvasFingerprintNoise = model.CanvasFingerprintNoise,
        AudioFingerprintNoise = model.AudioFingerprintNoise,
        WebRtcLaunchFlags = model.WebRtcLaunchFlags,
        StartupTabsJson = string.IsNullOrWhiteSpace(model.StartupTabsJson) ? "[]" : model.StartupTabsJson,
        ProxyPresetsJson = string.IsNullOrWhiteSpace(model.ProxyPresetsJson) ? "[]" : model.ProxyPresetsJson,
        FingerprintOverviewJson = string.IsNullOrWhiteSpace(model.FingerprintOverviewJson) ? "{}" : model.FingerprintOverviewJson,
        ScreenResolution = model.ScreenResolution,
        UseIpTimezone = model.UseIpTimezone,
        Timezone = model.Timezone,
        Languages = model.Languages,
        ActiveAdsCount = model.ActiveAdsCount,
        BlockedCount = model.BlockedCount,
        DraftsCount = model.DraftsCount,
        AdsStatsUpdatedAt = model.AdsStatsUpdatedAt,
        ProfileProvider = model.ProfileProvider.ToString(),
        AdsPowerProfileId = model.AdsPowerProfileId,
        AdsPowerProfileName = model.AdsPowerProfileName,
        AdsPowerApiBaseUrl = model.AdsPowerApiBaseUrl,
        AdsPowerApiKey = model.AdsPowerApiKey
    };

    private static AvitoAccount ToModel(AvitoAccountEntity entity) => new()
    {
        Id = entity.Id,
        DisplayName = entity.DisplayName,
        AvitoResponsesUrl = entity.AvitoResponsesUrl,
        BrowserProfilePath = entity.BrowserProfilePath,
        IsEnabled = entity.IsEnabled,
        Status = Enum.TryParse<AvitoAccountStatus>(entity.Status, out var status) ? status : AvitoAccountStatus.NotConfigured,
        LastAuthCheckAt = entity.LastAuthCheckAt,
        LastMonitoringAt = entity.LastMonitoringAt,
        LastErrorMessage = entity.LastErrorMessage,
        BrowserName = entity.BrowserName,
        BrowserVersion = entity.BrowserVersion,
        UserAgentDevice = entity.UserAgentDevice,
        UseWindowsOs = entity.UseWindowsOs,
        WindowsVersion = entity.WindowsVersion,
        UseMacOs = entity.UseMacOs,
        MacOsVersion = entity.MacOsVersion,
        UseLinuxOs = entity.UseLinuxOs,
        LinuxVersion = entity.LinuxVersion,
        UseAndroidOs = entity.UseAndroidOs,
        AndroidVersion = entity.AndroidVersion,
        UseIosOs = entity.UseIosOs,
        IosVersion = entity.IosVersion,
        AssignedUserAgent = entity.AssignedUserAgent,
        CookiesJson = entity.CookiesJson,
        ImportCookiesOnNextStart = entity.ImportCookiesOnNextStart,
        Notes = entity.Notes,
        ProxyAddress = entity.ProxyAddress,
        ProxyType = string.IsNullOrWhiteSpace(entity.ProxyType) ? "http" : entity.ProxyType,
        ProxyUsername = entity.ProxyUsername,
        ProxyPassword = entity.ProxyPassword,
        ProxyRotationUrl = entity.ProxyRotationUrl,
        BrowserLaunchArgs = entity.BrowserLaunchArgs ?? string.Empty,
        NavigatorPlatform = entity.NavigatorPlatform,
        DoNotTrack = entity.DoNotTrack,
        WebGlVendor = entity.WebGlVendor,
        WebGlRenderer = entity.WebGlRenderer,
        SpoofWebGl = entity.SpoofWebGl,
        CanvasFingerprintNoise = entity.CanvasFingerprintNoise,
        AudioFingerprintNoise = entity.AudioFingerprintNoise,
        WebRtcLaunchFlags = entity.WebRtcLaunchFlags,
        StartupTabsJson = string.IsNullOrWhiteSpace(entity.StartupTabsJson) ? "[]" : entity.StartupTabsJson,
        ProxyPresetsJson = string.IsNullOrWhiteSpace(entity.ProxyPresetsJson) ? "[]" : entity.ProxyPresetsJson,
        FingerprintOverviewJson = string.IsNullOrWhiteSpace(entity.FingerprintOverviewJson) ? "{}" : entity.FingerprintOverviewJson,
        ScreenResolution = string.IsNullOrWhiteSpace(entity.ScreenResolution) ? "1920x1080" : entity.ScreenResolution,
        UseIpTimezone = entity.UseIpTimezone,
        Timezone = string.IsNullOrWhiteSpace(entity.Timezone) ? "Europe/Moscow" : entity.Timezone,
        Languages = string.IsNullOrWhiteSpace(entity.Languages) ? "ru-RU,ru,en-US,en" : entity.Languages,
        ActiveAdsCount = entity.ActiveAdsCount,
        BlockedCount = entity.BlockedCount,
        DraftsCount = entity.DraftsCount,
        AdsStatsUpdatedAt = entity.AdsStatsUpdatedAt,
        ProfileProvider = Enum.TryParse<AvitoProfileProvider>(entity.ProfileProvider, out var provider)
            ? provider
            : AvitoProfileProvider.Local,
        AdsPowerProfileId = entity.AdsPowerProfileId,
        AdsPowerProfileName = entity.AdsPowerProfileName,
        AdsPowerApiBaseUrl = entity.AdsPowerApiBaseUrl,
        AdsPowerApiKey = entity.AdsPowerApiKey
    };

    private static void Map(AvitoAccount source, AvitoAccountEntity target)
    {
        target.DisplayName = source.DisplayName;
        target.AvitoResponsesUrl = source.AvitoResponsesUrl;
        target.BrowserProfilePath = source.BrowserProfilePath;
        target.IsEnabled = source.IsEnabled;
        target.Status = source.Status.ToString();
        target.LastAuthCheckAt = source.LastAuthCheckAt;
        target.LastMonitoringAt = source.LastMonitoringAt;
        target.LastErrorMessage = source.LastErrorMessage;
        target.BrowserName = source.BrowserName;
        target.BrowserVersion = source.BrowserVersion;
        target.UserAgentDevice = source.UserAgentDevice;
        target.UseWindowsOs = source.UseWindowsOs;
        target.WindowsVersion = source.WindowsVersion;
        target.UseMacOs = source.UseMacOs;
        target.MacOsVersion = source.MacOsVersion;
        target.UseLinuxOs = source.UseLinuxOs;
        target.LinuxVersion = source.LinuxVersion;
        target.UseAndroidOs = source.UseAndroidOs;
        target.AndroidVersion = source.AndroidVersion;
        target.UseIosOs = source.UseIosOs;
        target.IosVersion = source.IosVersion;
        target.AssignedUserAgent = source.AssignedUserAgent;
        target.CookiesJson = source.CookiesJson;
        target.ImportCookiesOnNextStart = source.ImportCookiesOnNextStart;
        target.Notes = source.Notes;
        target.ProxyAddress = source.ProxyAddress;
        target.ProxyType = string.IsNullOrWhiteSpace(source.ProxyType) ? "http" : source.ProxyType;
        target.ProxyUsername = source.ProxyUsername;
        target.ProxyPassword = source.ProxyPassword;
        target.ProxyRotationUrl = source.ProxyRotationUrl;
        target.BrowserLaunchArgs = source.BrowserLaunchArgs ?? string.Empty;
        target.NavigatorPlatform = source.NavigatorPlatform;
        target.DoNotTrack = source.DoNotTrack;
        target.WebGlVendor = source.WebGlVendor;
        target.WebGlRenderer = source.WebGlRenderer;
        target.SpoofWebGl = source.SpoofWebGl;
        target.CanvasFingerprintNoise = source.CanvasFingerprintNoise;
        target.AudioFingerprintNoise = source.AudioFingerprintNoise;
        target.WebRtcLaunchFlags = source.WebRtcLaunchFlags;
        target.StartupTabsJson = string.IsNullOrWhiteSpace(source.StartupTabsJson) ? "[]" : source.StartupTabsJson;
        target.ProxyPresetsJson = string.IsNullOrWhiteSpace(source.ProxyPresetsJson) ? "[]" : source.ProxyPresetsJson;
        target.FingerprintOverviewJson = string.IsNullOrWhiteSpace(source.FingerprintOverviewJson) ? "{}" : source.FingerprintOverviewJson;
        target.ScreenResolution = source.ScreenResolution;
        target.UseIpTimezone = source.UseIpTimezone;
        target.Timezone = source.Timezone;
        target.Languages = source.Languages;
        target.ActiveAdsCount = source.ActiveAdsCount;
        target.BlockedCount = source.BlockedCount;
        target.DraftsCount = source.DraftsCount;
        target.AdsStatsUpdatedAt = source.AdsStatsUpdatedAt;
        target.ProfileProvider = source.ProfileProvider.ToString();
        target.AdsPowerProfileId = source.AdsPowerProfileId;
        target.AdsPowerProfileName = source.AdsPowerProfileName;
        target.AdsPowerApiBaseUrl = source.AdsPowerApiBaseUrl;
        target.AdsPowerApiKey = source.AdsPowerApiKey;
    }

    private static CandidateResponseEntity ToEntity(CandidateResponse model) => new()
    {
        Id = model.Id,
        AccountId = model.AccountId,
        AccountName = model.AccountName,
        Source = model.Source,
        SourceResponseId = model.SourceResponseId,
        FullName = model.FullName,
        FirstName = model.FirstName,
        LastName = model.LastName,
        MiddleName = model.MiddleName,
        Age = model.Age,
        PhoneRaw = model.PhoneRaw,
        PhoneNormalized = model.PhoneNormalized,
        City = model.City,
        Vacancy = model.Vacancy,
        SourceUrl = model.VacancyUrl,
        VacancyUrl = model.VacancyUrl,
        MessengerUrl = model.MessengerUrl,
        Status = model.Status.ToString(),
        BitrixEntityType = model.BitrixEntityType,
        BitrixEntityId = model.BitrixEntityId,
        BitrixContactId = model.BitrixContactId,
        ErrorMessage = model.ErrorMessage,
        RawText = model.RawText,
        CreatedAt = model.CreatedAt,
        ProcessedAt = model.ProcessedAt
    };

    private static CandidateResponse ToModel(CandidateResponseEntity entity) => new()
    {
        Id = entity.Id,
        AccountId = entity.AccountId,
        AccountName = entity.AccountName,
        Source = entity.Source,
        SourceResponseId = entity.SourceResponseId,
        FullName = entity.FullName,
        FirstName = entity.FirstName,
        LastName = entity.LastName,
        MiddleName = entity.MiddleName,
        Age = entity.Age,
        PhoneRaw = entity.PhoneRaw,
        PhoneNormalized = entity.PhoneNormalized,
        City = entity.City,
        Vacancy = entity.Vacancy,
        VacancyUrl = string.IsNullOrWhiteSpace(entity.VacancyUrl) ? entity.SourceUrl : entity.VacancyUrl,
        MessengerUrl = entity.MessengerUrl,
        Status = Enum.TryParse<ResponseStatus>(entity.Status, out var status) ? status : ResponseStatus.New,
        BitrixEntityType = entity.BitrixEntityType,
        BitrixEntityId = entity.BitrixEntityId,
        BitrixContactId = entity.BitrixContactId,
        ErrorMessage = entity.ErrorMessage,
        RawText = entity.RawText,
        CreatedAt = entity.CreatedAt,
        ProcessedAt = entity.ProcessedAt
    };

    private static void Map(CandidateResponse source, CandidateResponseEntity target)
    {
        target.AccountId = source.AccountId;
        target.AccountName = source.AccountName;
        target.Source = source.Source;
        target.SourceResponseId = source.SourceResponseId;
        target.FullName = source.FullName;
        target.FirstName = source.FirstName;
        target.LastName = source.LastName;
        target.MiddleName = source.MiddleName;
        target.Age = source.Age;
        target.PhoneRaw = source.PhoneRaw;
        target.PhoneNormalized = source.PhoneNormalized;
        target.City = source.City;
        target.Vacancy = source.Vacancy;
        target.SourceUrl = source.VacancyUrl;
        target.VacancyUrl = source.VacancyUrl;
        target.MessengerUrl = source.MessengerUrl;
        target.Status = source.Status.ToString();
        target.BitrixEntityType = source.BitrixEntityType;
        target.BitrixEntityId = source.BitrixEntityId;
        target.BitrixContactId = source.BitrixContactId;
        target.ErrorMessage = source.ErrorMessage;
        target.RawText = source.RawText;
        target.CreatedAt = source.CreatedAt;
        target.ProcessedAt = source.ProcessedAt;
    }

    private static async Task EnsureAvitoAccountsSchemaAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var existingColumns = await GetTableColumnsAsync(db, "AvitoAccounts", cancellationToken);
        var alterStatements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ActiveAdsCount"] = "ALTER TABLE AvitoAccounts ADD COLUMN ActiveAdsCount INTEGER NOT NULL DEFAULT 0;",
            ["BlockedCount"] = "ALTER TABLE AvitoAccounts ADD COLUMN BlockedCount INTEGER NOT NULL DEFAULT 0;",
            ["DraftsCount"] = "ALTER TABLE AvitoAccounts ADD COLUMN DraftsCount INTEGER NOT NULL DEFAULT 0;",
            ["AdsStatsUpdatedAt"] = "ALTER TABLE AvitoAccounts ADD COLUMN AdsStatsUpdatedAt TEXT NULL;",
            ["BrowserName"] = "ALTER TABLE AvitoAccounts ADD COLUMN BrowserName TEXT NOT NULL DEFAULT '';",
            ["BrowserVersion"] = "ALTER TABLE AvitoAccounts ADD COLUMN BrowserVersion TEXT NOT NULL DEFAULT '146';",
            ["UserAgentDevice"] = "ALTER TABLE AvitoAccounts ADD COLUMN UserAgentDevice TEXT NOT NULL DEFAULT 'Все';",
            ["UseWindowsOs"] = "ALTER TABLE AvitoAccounts ADD COLUMN UseWindowsOs INTEGER NOT NULL DEFAULT 1;",
            ["WindowsVersion"] = "ALTER TABLE AvitoAccounts ADD COLUMN WindowsVersion TEXT NOT NULL DEFAULT 'Windows 10';",
            ["UseMacOs"] = "ALTER TABLE AvitoAccounts ADD COLUMN UseMacOs INTEGER NOT NULL DEFAULT 0;",
            ["MacOsVersion"] = "ALTER TABLE AvitoAccounts ADD COLUMN MacOsVersion TEXT NOT NULL DEFAULT 'All macOS';",
            ["UseLinuxOs"] = "ALTER TABLE AvitoAccounts ADD COLUMN UseLinuxOs INTEGER NOT NULL DEFAULT 0;",
            ["LinuxVersion"] = "ALTER TABLE AvitoAccounts ADD COLUMN LinuxVersion TEXT NOT NULL DEFAULT 'Linux x86_64';",
            ["UseAndroidOs"] = "ALTER TABLE AvitoAccounts ADD COLUMN UseAndroidOs INTEGER NOT NULL DEFAULT 0;",
            ["AndroidVersion"] = "ALTER TABLE AvitoAccounts ADD COLUMN AndroidVersion TEXT NOT NULL DEFAULT 'All Android';",
            ["UseIosOs"] = "ALTER TABLE AvitoAccounts ADD COLUMN UseIosOs INTEGER NOT NULL DEFAULT 0;",
            ["IosVersion"] = "ALTER TABLE AvitoAccounts ADD COLUMN IosVersion TEXT NOT NULL DEFAULT 'All iOS';",
            ["AssignedUserAgent"] = "ALTER TABLE AvitoAccounts ADD COLUMN AssignedUserAgent TEXT NULL;",
            ["CookiesJson"] = "ALTER TABLE AvitoAccounts ADD COLUMN CookiesJson TEXT NOT NULL DEFAULT '';",
            ["ImportCookiesOnNextStart"] = "ALTER TABLE AvitoAccounts ADD COLUMN ImportCookiesOnNextStart INTEGER NOT NULL DEFAULT 0;",
            ["Notes"] = "ALTER TABLE AvitoAccounts ADD COLUMN Notes TEXT NOT NULL DEFAULT '';",
            ["ProxyAddress"] = "ALTER TABLE AvitoAccounts ADD COLUMN ProxyAddress TEXT NULL;",
            ["ProxyType"] = "ALTER TABLE AvitoAccounts ADD COLUMN ProxyType TEXT NOT NULL DEFAULT 'http';",
            ["ScreenResolution"] = "ALTER TABLE AvitoAccounts ADD COLUMN ScreenResolution TEXT NULL;",
            ["UseIpTimezone"] = "ALTER TABLE AvitoAccounts ADD COLUMN UseIpTimezone INTEGER NOT NULL DEFAULT 0;",
            ["Timezone"] = "ALTER TABLE AvitoAccounts ADD COLUMN Timezone TEXT NULL;",
            ["Languages"] = "ALTER TABLE AvitoAccounts ADD COLUMN Languages TEXT NULL;",
            ["ProxyUsername"] = "ALTER TABLE AvitoAccounts ADD COLUMN ProxyUsername TEXT NULL;",
            ["ProxyPassword"] = "ALTER TABLE AvitoAccounts ADD COLUMN ProxyPassword TEXT NULL;",
            ["ProxyRotationUrl"] = "ALTER TABLE AvitoAccounts ADD COLUMN ProxyRotationUrl TEXT NULL;",
            ["BrowserLaunchArgs"] = "ALTER TABLE AvitoAccounts ADD COLUMN BrowserLaunchArgs TEXT NOT NULL DEFAULT '';",
            ["NavigatorPlatform"] = "ALTER TABLE AvitoAccounts ADD COLUMN NavigatorPlatform TEXT NULL;",
            ["DoNotTrack"] = "ALTER TABLE AvitoAccounts ADD COLUMN DoNotTrack INTEGER NOT NULL DEFAULT 0;",
            ["WebGlVendor"] = "ALTER TABLE AvitoAccounts ADD COLUMN WebGlVendor TEXT NULL;",
            ["WebGlRenderer"] = "ALTER TABLE AvitoAccounts ADD COLUMN WebGlRenderer TEXT NULL;",
            ["SpoofWebGl"] = "ALTER TABLE AvitoAccounts ADD COLUMN SpoofWebGl INTEGER NOT NULL DEFAULT 0;",
            ["CanvasFingerprintNoise"] = "ALTER TABLE AvitoAccounts ADD COLUMN CanvasFingerprintNoise INTEGER NOT NULL DEFAULT 1;",
            ["AudioFingerprintNoise"] = "ALTER TABLE AvitoAccounts ADD COLUMN AudioFingerprintNoise INTEGER NOT NULL DEFAULT 1;",
            ["WebRtcLaunchFlags"] = "ALTER TABLE AvitoAccounts ADD COLUMN WebRtcLaunchFlags TEXT NULL;",
            ["StartupTabsJson"] = "ALTER TABLE AvitoAccounts ADD COLUMN StartupTabsJson TEXT NOT NULL DEFAULT '[]';",
            ["ProxyPresetsJson"] = "ALTER TABLE AvitoAccounts ADD COLUMN ProxyPresetsJson TEXT NOT NULL DEFAULT '[]';",
            // '{{}}' — экранирование для ExecuteSqlRaw (иначе '{}' ломает string.Format).
            ["FingerprintOverviewJson"] = "ALTER TABLE AvitoAccounts ADD COLUMN FingerprintOverviewJson TEXT NOT NULL DEFAULT '{{}}';",
            ["ProfileProvider"] = "ALTER TABLE AvitoAccounts ADD COLUMN ProfileProvider TEXT NOT NULL DEFAULT 'Local';",
            ["AdsPowerProfileId"] = "ALTER TABLE AvitoAccounts ADD COLUMN AdsPowerProfileId TEXT NULL;",
            ["AdsPowerProfileName"] = "ALTER TABLE AvitoAccounts ADD COLUMN AdsPowerProfileName TEXT NULL;",
            ["AdsPowerApiBaseUrl"] = "ALTER TABLE AvitoAccounts ADD COLUMN AdsPowerApiBaseUrl TEXT NULL;",
            ["AdsPowerApiKey"] = "ALTER TABLE AvitoAccounts ADD COLUMN AdsPowerApiKey TEXT NULL;"
        };

        foreach (var (columnName, statement) in alterStatements)
        {
            if (existingColumns.Contains(columnName))
            {
                continue;
            }

            await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }
    }

    private static async Task EnsureCandidateResponsesSchemaAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_CandidateResponses_CreatedAt ON CandidateResponses (CreatedAt);",
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS IX_CandidateResponses_AccountId_SourceResponseId;",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_CandidateResponses_AccountId_SourceResponseId
            ON CandidateResponses (AccountId, SourceResponseId)
            WHERE length(trim(SourceResponseId)) > 0;
            """,
            cancellationToken);

        var existingColumns = await GetTableColumnsAsync(db, "CandidateResponses", cancellationToken);
        if (!existingColumns.Contains("MessengerUrl"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE CandidateResponses ADD COLUMN MessengerUrl TEXT NOT NULL DEFAULT '';",
                cancellationToken);
        }

        existingColumns = await GetTableColumnsAsync(db, "CandidateResponses", cancellationToken);
        if (!existingColumns.Contains("SourceUrl"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE CandidateResponses ADD COLUMN SourceUrl TEXT NOT NULL DEFAULT '';",
                cancellationToken);
        }

        existingColumns = await GetTableColumnsAsync(db, "CandidateResponses", cancellationToken);
        if (!existingColumns.Contains("VacancyUrl"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE CandidateResponses ADD COLUMN VacancyUrl TEXT NOT NULL DEFAULT '';",
                cancellationToken);
        }

        existingColumns = await GetTableColumnsAsync(db, "CandidateResponses", cancellationToken);
        if (!existingColumns.Contains("BitrixContactId"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE CandidateResponses ADD COLUMN BitrixContactId TEXT NOT NULL DEFAULT '';",
                cancellationToken);
        }
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(
        AppDbContext db,
        string tableName,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldCloseConnection = connection.State != System.Data.ConnectionState.Open;
        if (shouldCloseConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"", StringComparison.Ordinal)}\");";

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(1))
                {
                    columns.Add(reader.GetString(1));
                }
            }

            return columns;
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await connection.CloseAsync();
            }
        }
    }
}
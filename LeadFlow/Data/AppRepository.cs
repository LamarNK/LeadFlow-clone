using LeadFlow.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LeadFlow.Data;

public sealed class AppRepository(IDbContextFactory<AppDbContext> dbContextFactory)
{
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

    public async Task SaveCandidateAsync(CandidateResponse response, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.CandidateResponses.FindAsync([response.Id], cancellationToken);
        if (existing is null)
        {
            db.CandidateResponses.Add(ToEntity(response));
        }
        else
        {
            Map(response, existing);
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
        var today = DateTime.UtcNow.Date;
        var responseSummary = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.CreatedAt >= today)
            .GroupBy(static _ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Sent = g.Count(x => x.Status == nameof(ResponseStatus.Sent)),
                InProgress = g.Count(x => x.Status == nameof(ResponseStatus.InProgress)),
                Duplicates = g.Count(x => x.Status == nameof(ResponseStatus.Duplicate)),
                Errors = g.Count(x => x.Status == nameof(ResponseStatus.Error))
            })
            .FirstOrDefaultAsync(cancellationToken);

        var activityBuckets = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.CreatedAt >= today)
            .GroupBy(x => x.CreatedAt.Hour / 3)
            .Select(g => new
            {
                Bucket = g.Key,
                Total = g.Count(),
                Sent = g.Count(x => x.Status == nameof(ResponseStatus.Sent)),
                Duplicates = g.Count(x => x.Status == nameof(ResponseStatus.Duplicate)),
                Errors = g.Count(x => x.Status == nameof(ResponseStatus.Error))
            })
            .ToListAsync(cancellationToken);

        var accountSummary = await db.AvitoAccounts
            .AsNoTracking()
            .GroupBy(static _ => 1)
            .Select(g => new
            {
                Connected = g.Count(x => x.IsEnabled),
                RequiresAuthorization = g.Count(x => x.Status == nameof(AvitoAccountStatus.RequiresLogin)),
                ActiveAds = g.Sum(x => x.ActiveAdsCount),
                BlockedAds = g.Sum(x => x.BlockedCount),
                Drafts = g.Sum(x => x.DraftsCount)
            })
            .FirstOrDefaultAsync(cancellationToken);

        var totalToday = responseSummary?.Total ?? 0;
        var stats = new DashboardStats
        {
            NewResponses = totalToday,
            TotalToday = totalToday,
            SentToCrm = responseSummary?.Sent ?? 0,
            InProgress = responseSummary?.InProgress ?? 0,
            Duplicates = responseSummary?.Duplicates ?? 0,
            Errors = responseSummary?.Errors ?? 0,
            ConnectedAccounts = accountSummary?.Connected ?? 0,
            RequiresAuthorization = accountSummary?.RequiresAuthorization ?? 0,
            ActiveAdsCount = accountSummary?.ActiveAds ?? 0,
            BlockedAdsCount = accountSummary?.BlockedAds ?? 0,
            DraftsCount = accountSummary?.Drafts ?? 0
        };

        for (var hour = 0; hour < 24; hour += 3)
        {
            var bucket = activityBuckets.FirstOrDefault(x => x.Bucket == hour / 3);
            stats.Activity.Add(new ActivityPoint
            {
                Label = $"{hour:00}:00",
                NewCount = bucket?.Total ?? 0,
                SentCount = bucket?.Sent ?? 0,
                DuplicateCount = bucket?.Duplicates ?? 0,
                ErrorCount = bucket?.Errors ?? 0
            });
        }

        return stats;
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
        ActiveAdsCount = model.ActiveAdsCount,
        BlockedCount = model.BlockedCount,
        DraftsCount = model.DraftsCount,
        AdsStatsUpdatedAt = model.AdsStatsUpdatedAt
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
        ActiveAdsCount = entity.ActiveAdsCount,
        BlockedCount = entity.BlockedCount,
        DraftsCount = entity.DraftsCount,
        AdsStatsUpdatedAt = entity.AdsStatsUpdatedAt
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
        target.ActiveAdsCount = source.ActiveAdsCount;
        target.BlockedCount = source.BlockedCount;
        target.DraftsCount = source.DraftsCount;
        target.AdsStatsUpdatedAt = source.AdsStatsUpdatedAt;
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
        SourceUrl = model.SourceUrl,
        Status = model.Status.ToString(),
        BitrixEntityType = model.BitrixEntityType,
        BitrixEntityId = model.BitrixEntityId,
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
        SourceUrl = entity.SourceUrl,
        Status = Enum.TryParse<ResponseStatus>(entity.Status, out var status) ? status : ResponseStatus.New,
        BitrixEntityType = entity.BitrixEntityType,
        BitrixEntityId = entity.BitrixEntityId,
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
        target.SourceUrl = source.SourceUrl;
        target.Status = source.Status.ToString();
        target.BitrixEntityType = source.BitrixEntityType;
        target.BitrixEntityId = source.BitrixEntityId;
        target.ErrorMessage = source.ErrorMessage;
        target.RawText = source.RawText;
        target.CreatedAt = source.CreatedAt;
        target.ProcessedAt = source.ProcessedAt;
    }

    private static async Task EnsureAvitoAccountsSchemaAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var alterStatements = new[]
        {
            "ALTER TABLE AvitoAccounts ADD COLUMN ActiveAdsCount INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE AvitoAccounts ADD COLUMN BlockedCount INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE AvitoAccounts ADD COLUMN DraftsCount INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE AvitoAccounts ADD COLUMN AdsStatsUpdatedAt TEXT NULL;"
        };

        foreach (var statement in alterStatements)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
            }
        }
    }

    private static Task EnsureCandidateResponsesSchemaAsync(AppDbContext db, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_CandidateResponses_CreatedAt ON CandidateResponses (CreatedAt);",
            cancellationToken);
}

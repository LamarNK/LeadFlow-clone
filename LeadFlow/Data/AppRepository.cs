using LeadFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace LeadFlow.Data;

public sealed class AppRepository(IDbContextFactory<AppDbContext> dbContextFactory)
{
    public async Task InitializeAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);

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
        return await db.AvitoAccounts.OrderBy(x => x.DisplayName).Select(x => ToModel(x)).ToListAsync(cancellationToken);
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
        return await db.CandidateResponses.OrderByDescending(x => x.CreatedAt).Take(take).Select(x => ToModel(x)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProcessingLogItem>> GetRecentLogsAsync(int take, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ProcessingLogs
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
        var responsesToday = await db.CandidateResponses.Where(x => x.CreatedAt >= today).ToListAsync(cancellationToken);
        var accounts = await db.AvitoAccounts.ToListAsync(cancellationToken);
        var stats = new DashboardStats
        {
            NewResponses = responsesToday.Count(x => x.Status == nameof(ResponseStatus.New)),
            TotalToday = responsesToday.Count,
            SentToCrm = responsesToday.Count(x => x.Status == nameof(ResponseStatus.Sent)),
            InProgress = responsesToday.Count(x => x.Status == nameof(ResponseStatus.InProgress)),
            Duplicates = responsesToday.Count(x => x.Status == nameof(ResponseStatus.Duplicate)),
            Errors = responsesToday.Count(x => x.Status == nameof(ResponseStatus.Error)),
            ConnectedAccounts = accounts.Count(x => x.IsEnabled),
            RequiresAuthorization = accounts.Count(x => x.Status == nameof(AvitoAccountStatus.RequiresLogin))
        };

        for (var hour = 0; hour < 24; hour += 3)
        {
            var from = today.AddHours(hour);
            var to = from.AddHours(3);
            var bucket = responsesToday.Where(x => x.CreatedAt >= from && x.CreatedAt < to).ToList();
            stats.Activity.Add(new ActivityPoint
            {
                Label = $"{hour:00}:00",
                NewCount = bucket.Count(x => x.Status == nameof(ResponseStatus.New)),
                SentCount = bucket.Count(x => x.Status == nameof(ResponseStatus.Sent)),
                DuplicateCount = bucket.Count(x => x.Status == nameof(ResponseStatus.Duplicate)),
                ErrorCount = bucket.Count(x => x.Status == nameof(ResponseStatus.Error))
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
        LastErrorMessage = model.LastErrorMessage
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
        LastErrorMessage = entity.LastErrorMessage
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
}

using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class ResponsesQueryService(
    OrbitaDbContext db,
    OfficeBitrixWebhookResolver bitrixWebhooks)
{
    public Task<ResponsesPageDto> GetPageAsync(
        OfficeScope scope,
        Guid? officeFilter,
        string? status,
        string? search,
        string? vacancy,
        Guid? workerId,
        Guid? accountId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default) =>
        GetPageInternalAsync(scope, officeFilter, status, search, vacancy, workerId, accountId, fromUtc, toUtc, page, pageSize, sort, sortDir, ct);

    public async Task<ResponsesSummaryDto> GetSummaryAsync(
        OfficeScope scope,
        Guid? officeFilter,
        string? status,
        string? search,
        string? vacancy,
        Guid? workerId,
        Guid? accountId,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken ct = default)
    {
        var query = BuildFilteredQuery(scope, officeFilter, status, search, vacancy, workerId, accountId, fromUtc, toUtc);
        var total = await query.CountAsync(ct);
        if (total == 0)
        {
            return new ResponsesSummaryDto(0, 0, 0, 0, null);
        }

        var duplicates = await query.CountAsync(x => x.Status == ResponseStatuses.Duplicate, ct);
        var unique = total - duplicates;
        var uniqueAuthors = await query
            .Where(x => !string.IsNullOrWhiteSpace(x.PhoneNormalized))
            .Select(x => x.PhoneNormalized)
            .Distinct()
            .CountAsync(ct);

        double? avgMinutes = null;
        var processed = await query
            .Where(x => x.ProcessedAt != null)
            .Select(x => new { x.CreatedAt, ProcessedAt = x.ProcessedAt!.Value })
            .ToListAsync(ct);
        if (processed.Count > 0)
        {
            avgMinutes = processed.Average(x => (x.ProcessedAt - x.CreatedAt).TotalMinutes);
        }

        return new ResponsesSummaryDto(
            total,
            unique,
            duplicates,
            uniqueAuthors,
            avgMinutes > 0 ? avgMinutes : null);
    }

    public async Task<IReadOnlyList<ResponseFilterAccountDto>> GetFilterAccountsAsync(
        OfficeScope scope,
        Guid? officeFilter,
        CancellationToken ct = default)
    {
        var query = ApplyOfficeFilter(db.CandidateResponses.AsNoTracking(), scope, officeFilter);
        var rows = await query
            .Select(x => new { x.AccountId, x.AccountName })
            .Distinct()
            .OrderBy(x => x.AccountName)
            .ToListAsync(ct);
        return rows.Select(x => new ResponseFilterAccountDto(x.AccountId, x.AccountName)).ToList();
    }

    public async Task<ResponseDetailDto?> GetDetailAsync(
        Guid id,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var entity = await db.CandidateResponses
            .AsNoTracking()
            .Include(x => x.Worker)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null || !scope.CanAccessOffice(entity.OfficeId))
        {
            return null;
        }

        var portalHost = await bitrixWebhooks.ResolvePortalHostAsync(entity.OfficeId, ct);
        var subProfilesJson = await db.WorkerAccounts
            .AsNoTracking()
            .Where(a => a.AccountId == entity.AccountId)
            .Select(a => a.SubProfilesJson)
            .FirstOrDefaultAsync(ct);
        var nameLookup = SubProfileNameResolver.BuildLookup([(entity.AccountId, subProfilesJson ?? "[]")]);
        var subProfileName = CoalesceSubProfileName(
            entity.AvitoSubProfileName,
            nameLookup,
            entity.AccountId,
            entity.AvitoSubProfileId);
        return MapDetail(entity, portalHost, subProfileName);
    }

    private async Task<ResponsesPageDto> GetPageInternalAsync(
        OfficeScope scope,
        Guid? officeFilter,
        string? status,
        string? search,
        string? vacancy,
        Guid? workerId,
        Guid? accountId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        string? sort,
        string? sortDir,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = BuildFilteredQuery(scope, officeFilter, status, search, vacancy, workerId, accountId, fromUtc, toUtc);
        var total = await query.CountAsync(ct);
        var rows = await ApplyOrdering(query, sort, sortDir)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.OfficeId,
                x.WorkerId,
                WorkerName = x.Worker.DisplayName,
                x.AccountId,
                x.AccountName,
                x.Source,
                x.SourceResponseId,
                x.FullName,
                x.Age,
                x.PhoneRaw,
                x.PhoneNormalized,
                x.Vacancy,
                x.VacancyUrl,
                x.MessengerUrl,
                x.City,
                x.Status,
                x.IsLocalDuplicate,
                x.IsBitrixDuplicate,
                x.BitrixEntityId,
                x.BitrixEntityType,
                x.AvitoSubProfileId,
                x.AvitoSubProfileName,
                x.CreatedAt,
                x.ProcessedAt
            })
            .ToListAsync(ct);

        var portalByOffice = new Dictionary<Guid, string?>();
        foreach (var officeId in rows.Select(x => x.OfficeId).Distinct())
        {
            portalByOffice[officeId] = await bitrixWebhooks.ResolvePortalHostAsync(officeId, ct);
        }

        var accountIds = rows.Select(x => x.AccountId).Distinct().ToList();
        var subProfileRows = accountIds.Count == 0
            ? []
            : await db.WorkerAccounts
                .AsNoTracking()
                .Where(a => accountIds.Contains(a.AccountId))
                .Select(a => new { a.AccountId, a.SubProfilesJson })
                .ToListAsync(ct);

        var nameLookup = SubProfileNameResolver.BuildLookup(
            subProfileRows.Select(x => (x.AccountId, x.SubProfilesJson)));

        var items = rows
            .Select(x =>
            {
                portalByOffice.TryGetValue(x.OfficeId, out var portalHost);
                var bitrixEntityUrl = BitrixPortalLinks.TryBuildEntityDetailsUrl(
                    portalHost,
                    x.BitrixEntityType,
                    x.BitrixEntityId);
                return new ResponseListItemDto(
                    x.Id,
                    x.OfficeId,
                    x.WorkerId,
                    x.WorkerName,
                    x.AccountId,
                    x.AccountName,
                    x.Source,
                    x.SourceResponseId,
                    x.FullName,
                    x.Age,
                    x.PhoneRaw,
                    x.PhoneNormalized,
                    x.Vacancy,
                    x.VacancyUrl,
                    x.MessengerUrl,
                    x.City,
                    x.Status,
                    x.IsLocalDuplicate,
                    x.IsBitrixDuplicate,
                    x.BitrixEntityId,
                    string.IsNullOrWhiteSpace(x.BitrixEntityType) ? null : x.BitrixEntityType,
                    bitrixEntityUrl,
                    x.AvitoSubProfileId,
                    CoalesceSubProfileName(
                        x.AvitoSubProfileName,
                        nameLookup,
                        x.AccountId,
                        x.AvitoSubProfileId),
                    x.CreatedAt,
                    x.ProcessedAt);
            })
            .ToList();

        return new ResponsesPageDto(items, total, page, pageSize);
    }

    private static readonly HashSet<string> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "time", "author", "phone", "city", "age", "vacancy", "account", "status", "source"
    };

    private static IQueryable<CandidateResponseEntity> ApplyOrdering(
        IQueryable<CandidateResponseEntity> query,
        string? sort,
        string? sortDir)
    {
        var column = NormalizeSortColumn(sort);
        var descending = ResolveDescending(column, sortDir);

        var ordered = column switch
        {
            "author" => descending
                ? query.OrderByDescending(x => x.FullName)
                : query.OrderBy(x => x.FullName),
            "phone" => descending
                ? query.OrderByDescending(x => x.PhoneNormalized).ThenByDescending(x => x.PhoneRaw)
                : query.OrderBy(x => x.PhoneNormalized).ThenBy(x => x.PhoneRaw),
            "city" => descending
                ? query.OrderByDescending(x => x.City)
                : query.OrderBy(x => x.City),
            "age" => descending
                ? query.OrderByDescending(x => x.Age)
                : query.OrderBy(x => x.Age),
            "vacancy" => descending
                ? query.OrderByDescending(x => x.Vacancy)
                : query.OrderBy(x => x.Vacancy),
            "account" => descending
                ? query.OrderByDescending(x => x.AccountName)
                : query.OrderBy(x => x.AccountName),
            "status" => descending
                ? query.OrderByDescending(x => x.Status)
                : query.OrderBy(x => x.Status),
            "source" => descending
                ? query.OrderByDescending(x => x.Source)
                : query.OrderBy(x => x.Source),
            _ => descending
                ? query.OrderByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.CreatedAt)
        };

        return column == "time"
            ? ordered
            : descending
                ? ordered.ThenByDescending(x => x.CreatedAt)
                : ordered.ThenBy(x => x.CreatedAt);
    }

    private static string NormalizeSortColumn(string? sort) =>
        !string.IsNullOrWhiteSpace(sort) && AllowedSortColumns.Contains(sort)
            ? sort.ToLowerInvariant()
            : "time";

    private static bool ResolveDescending(string column, string? dir)
    {
        if (string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return column == "time";
    }

    private IQueryable<CandidateResponseEntity> BuildFilteredQuery(
        OfficeScope scope,
        Guid? officeFilter,
        string? status,
        string? search,
        string? vacancy,
        Guid? workerId,
        Guid? accountId,
        DateTime? fromUtc,
        DateTime? toUtc)
    {
        var query = db.CandidateResponses
            .AsNoTracking()
            .Include(x => x.Worker)
            .AsQueryable();

        query = ApplyOfficeFilter(query, scope, officeFilter);

        if (workerId is Guid wid)
        {
            query = query.Where(x => x.WorkerId == wid);
        }

        if (accountId is Guid aid)
        {
            query = query.Where(x => x.AccountId == aid);
        }

        query = ApplyStatusFilter(query, status);

        if (fromUtc is not null)
        {
            query = query.Where(x => x.CreatedAt >= fromUtc.Value);
        }

        if (toUtc is not null)
        {
            query = query.Where(x => x.CreatedAt < toUtc.Value);
        }

        query = ApplySearchFilter(query, search);
        query = ApplyVacancyFilter(query, vacancy);

        return query;
    }

    private static IQueryable<CandidateResponseEntity> ApplySearchFilter(
        IQueryable<CandidateResponseEntity> query,
        string? search)
    {
        foreach (var token in SearchQueryNormalizer.Tokenize(search))
        {
            var pattern = SearchQueryNormalizer.ToILikePattern(token);
            var phoneDigits = SearchQueryNormalizer.ExtractDigits(token);
            var hasPhone = phoneDigits.Length >= 4;
            var phonePattern = $"%{phoneDigits}%";

            query = query.Where(x =>
                EF.Functions.ILike(x.FullName, pattern)
                || EF.Functions.ILike(x.PhoneRaw, pattern)
                || EF.Functions.ILike(x.PhoneNormalized, pattern)
                || EF.Functions.ILike(x.Vacancy, pattern)
                || EF.Functions.ILike(x.City, pattern)
                || EF.Functions.ILike(x.AccountName, pattern)
                || (hasPhone && (
                    EF.Functions.ILike(x.PhoneRaw, phonePattern)
                    || EF.Functions.ILike(x.PhoneNormalized, phonePattern))));
        }

        return query;
    }

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

    private static IQueryable<CandidateResponseEntity> ApplyStatusFilter(
        IQueryable<CandidateResponseEntity> query,
        string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return query;
        }

        return status.ToLowerInvariant() switch
        {
            "unique" => query.Where(x => x.Status != ResponseStatuses.Duplicate),
            "duplicate" => query.Where(x => x.Status == ResponseStatuses.Duplicate),
            "sent" => query.Where(x => x.Status == ResponseStatuses.Sent),
            "action_required" => query.Where(x => x.Status == ResponseStatuses.ActionRequired),
            "error" => query.Where(x => x.Status == ResponseStatuses.Error),
            "exclude-duplicates" => query.Where(x => x.Status != ResponseStatuses.Duplicate),
            _ => query.Where(x => x.Status == status)
        };
    }

    private static IQueryable<CandidateResponseEntity> ApplyOfficeFilter(
        IQueryable<CandidateResponseEntity> query,
        OfficeScope scope,
        Guid? officeFilter)
    {
        var effectiveOfficeId = scope.ResolveFilter(officeFilter);
        if (effectiveOfficeId is Guid officeId)
        {
            return query.Where(x => x.OfficeId == officeId);
        }

        return scope.IsGlobalAdmin ? query : query.Where(_ => false);
    }

    private static string? CoalesceSubProfileName(
        string? storedName,
        IReadOnlyDictionary<(Guid AccountId, string SubProfileId), string> lookup,
        Guid accountId,
        string? subProfileId)
    {
        if (!string.IsNullOrWhiteSpace(storedName))
        {
            return storedName.Trim();
        }

        return ResolveSubProfileName(lookup, accountId, subProfileId);
    }

    private static string? ResolveSubProfileName(
        IReadOnlyDictionary<(Guid AccountId, string SubProfileId), string> lookup,
        Guid accountId,
        string? subProfileId)
    {
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return null;
        }

        var id = subProfileId.Trim();
        return lookup.TryGetValue((accountId, id), out var name) ? name : null;
    }

    private static ResponseDetailDto MapDetail(
        CandidateResponseEntity entity,
        string? portalHost,
        string? subProfileName = null)
    {
        var bitrixEntityUrl = BitrixPortalLinks.TryBuildEntityDetailsUrl(
            portalHost,
            entity.BitrixEntityType,
            entity.BitrixEntityId);
        return new(
            entity.Id,
            entity.OfficeId,
            entity.WorkerId,
            entity.Worker.DisplayName,
            entity.AccountId,
            entity.AccountName,
            entity.Source,
            entity.SourceResponseId,
            entity.FullName,
            entity.FirstName,
            entity.LastName,
            entity.MiddleName,
            entity.Age,
            entity.PhoneRaw,
            entity.PhoneNormalized,
            entity.City,
            entity.Vacancy,
            entity.VacancyUrl,
            entity.MessengerUrl,
            entity.AvitoSubProfileId,
            subProfileName,
            entity.RawText,
            entity.ChatMessagesJson,
            entity.Status,
            entity.IsLocalDuplicate,
            entity.IsBitrixDuplicate,
            entity.DuplicateSummary,
            entity.BitrixEntityId,
            string.IsNullOrWhiteSpace(entity.BitrixEntityType) ? null : entity.BitrixEntityType,
            bitrixEntityUrl,
            entity.BitrixContactId,
            entity.ErrorMessage,
            entity.CreatedAt,
            entity.ProcessedAt);
    }
}
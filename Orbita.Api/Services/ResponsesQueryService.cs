using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class ResponsesQueryService(OrbitaDbContext db)
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
        CancellationToken ct = default) =>
        GetPageInternalAsync(scope, officeFilter, status, search, vacancy, workerId, accountId, fromUtc, toUtc, page, pageSize, ct);

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
        return await query
            .GroupBy(x => new { x.AccountId, x.AccountName })
            .Select(g => new ResponseFilterAccountDto(g.Key.AccountId, g.Key.AccountName))
            .OrderBy(x => x.AccountName)
            .ToListAsync(ct);
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

        return MapDetail(entity);
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
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = BuildFilteredQuery(scope, officeFilter, status, search, vacancy, workerId, accountId, fromUtc, toUtc);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ResponseListItemDto(
                x.Id,
                x.OfficeId,
                x.WorkerId,
                x.Worker.DisplayName,
                x.AccountId,
                x.AccountName,
                x.Source,
                x.SourceResponseId,
                x.FullName,
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
                x.CreatedAt,
                x.ProcessedAt))
            .ToListAsync(ct);

        return new ResponsesPageDto(items, total, page, pageSize);
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

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x =>
                x.FullName.Contains(term)
                || x.PhoneRaw.Contains(term)
                || x.PhoneNormalized.Contains(term)
                || x.Vacancy.Contains(term)
                || x.City.Contains(term)
                || x.AccountName.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(vacancy))
        {
            var adTerm = vacancy.Trim();
            query = query.Where(x =>
                x.Vacancy.Contains(adTerm)
                || x.SourceResponseId.Contains(adTerm)
                || x.VacancyUrl.Contains(adTerm));
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
            "error" => query.Where(x =>
                x.Status == ResponseStatuses.Error || x.Status == ResponseStatuses.ActionRequired),
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

    private static ResponseDetailDto MapDetail(CandidateResponseEntity entity) => new(
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
        entity.RawText,
        entity.Status,
        entity.IsLocalDuplicate,
        entity.IsBitrixDuplicate,
        entity.DuplicateSummary,
        entity.BitrixEntityId,
        entity.BitrixContactId,
        entity.ErrorMessage,
        entity.CreatedAt,
        entity.ProcessedAt);
}
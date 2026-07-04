using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class PanelAuditService(OrbitaDbContext db)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task LogAsync(
        string? actorUserId,
        string? actorEmail,
        string action,
        string? targetType = null,
        string? targetId = null,
        string? details = null,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        db.PanelAuditLogs.Add(new PanelAuditLogEntity
        {
            TimestampUtc = DateTime.UtcNow,
            ActorUserId = actorUserId,
            ActorEmail = actorEmail,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = details,
            IpAddress = ipAddress
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<PanelAuditPageDto> SearchAsync(
        string? searchText,
        string? action,
        DateOnly? date,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.PanelAuditLogs.AsNoTracking().AsQueryable();
        if (date.HasValue)
        {
            var startUtc = date.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var endUtc = startUtc.AddDays(1);
            query = query.Where(x => x.TimestampUtc >= startUtc && x.TimestampUtc < endUtc);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(x => x.Action == action);
        }

        foreach (var token in SearchQueryNormalizer.Tokenize(searchText))
        {
            var pattern = SearchQueryNormalizer.ToILikePattern(token);
            query = query.Where(x =>
                (x.ActorEmail != null && EF.Functions.ILike(x.ActorEmail, pattern))
                || (x.Details != null && EF.Functions.ILike(x.Details, pattern))
                || (x.TargetId != null && EF.Functions.ILike(x.TargetId, pattern))
                || EF.Functions.ILike(x.Action, pattern));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.TimestampUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new PanelAuditEntryDto(
                x.Id,
                x.TimestampUtc,
                x.ActorEmail,
                x.Action,
                x.TargetType,
                x.TargetId,
                x.Details,
                x.IpAddress))
            .ToListAsync(ct);

        return new PanelAuditPageDto(items, total, page, pageSize);
    }
}
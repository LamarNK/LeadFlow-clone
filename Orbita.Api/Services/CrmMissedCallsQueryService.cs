using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>Call history is the source of truth, not the limited/readable notification feed.</summary>
public sealed class CrmMissedCallsQueryService(OrbitaDbContext db)
{
    public async Task<CrmMissedCallsDto?> GetAsync(Guid? officeId, string userId,
        bool elevated, bool globalAdmin, DateTime fromUtc, DateTime toUtc,
        string? managerUserId = null, string? status = null, int page = 1,
        CancellationToken ct = default, Guid? callId = null)
    {
        if (string.IsNullOrWhiteSpace(userId) || toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(366))
            return null;
        if (!globalAdmin && (officeId is null || !await db.PanelUserProfiles.AnyAsync(
            x => x.UserId == userId && x.OfficeId == officeId, ct))) return null;

        var query = from call in db.CrmCalls.AsNoTracking()
                    join office in db.Offices on call.OfficeId equals office.Id
                    from card in db.CrmCandidateCards.Where(c => c.Id == call.CardId
                        && c.OfficeId == call.OfficeId).DefaultIfEmpty()
                    where office.IsEnabled && office.CrmEnabled
                        && (officeId == null || call.OfficeId == officeId)
                        && call.Direction == CrmCallDirections.Incoming
                        && (callId != null ? call.Id == callId
                            : call.StartedAtUtc >= fromUtc && call.StartedAtUtc < toUtc)
                        && (call.Status == CrmCallStatuses.Missed || call.Status == CrmCallStatuses.Rejected
                            || call.Status == CrmCallStatuses.Failed)
                    select new
                    {
                        Call = call, Card = card, OfficeName = office.Name,
                        ResponsibleId = card != null && card.ManagerUserId != null
                            ? card.ManagerUserId : call.ManagerUserId
                    };
        if (!elevated && !globalAdmin) query = query.Where(x => x.ResponsibleId == userId);

        var managerIds = await query.Where(x => x.ResponsibleId != null)
            .Select(x => x.ResponsibleId!).Distinct().ToArrayAsync(ct);
        var names = await db.PanelUserProfiles.AsNoTracking().Where(x => managerIds.Contains(x.UserId))
            .Select(x => new { x.UserId, x.FullName }).ToDictionaryAsync(x => x.UserId, x => x.FullName, ct);
        string ManagerName(string id) => names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name : "Сотрудник";
        var managers = managerIds.Select(id => new CrmMissedCallManagerDto(id, ManagerName(id)))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToArray();
        if ((elevated || globalAdmin) && !string.IsNullOrWhiteSpace(managerUserId))
            query = query.Where(x => x.ResponsibleId == managerUserId);

        var counts = await query.GroupBy(x => x.Call.Status)
            .Select(x => new { Status = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Status, x => x.Count, ct);
        if (CrmCallStatuses.IsUnanswered(status)) query = query.Where(x => x.Call.Status == status);
        var total = await query.CountAsync(ct);
        const int pageSize = 30;
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var rows = await query.OrderByDescending(x => x.Call.StartedAtUtc).ThenByDescending(x => x.Call.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Call.Id,
                CardId = x.Card != null && (elevated || globalAdmin || x.Card.ManagerUserId == userId)
                    ? (Guid?)x.Card.Id : null,
                CandidateName = x.Card != null && (elevated || globalAdmin || x.Card.ManagerUserId == userId)
                    ? x.Card.Response.FullName : null,
                Phone = x.Call.ClientPhoneNormalized, x.Call.CalledPhone, x.Call.StartedAtUtc,
                x.Call.Status, x.ResponsibleId, x.OfficeName
            }).ToListAsync(ct);
        return new(total, counts.GetValueOrDefault(CrmCallStatuses.Missed),
            counts.GetValueOrDefault(CrmCallStatuses.Rejected), counts.GetValueOrDefault(CrmCallStatuses.Failed),
            page, pageSize, rows.Select(x => new CrmMissedCallDto(x.Id, x.CardId, x.CandidateName,
                x.Phone, x.CalledPhone, x.StartedAtUtc, x.Status,
                x.ResponsibleId == null ? null : ManagerName(x.ResponsibleId), x.OfficeName)).ToArray(), managers);
    }
}

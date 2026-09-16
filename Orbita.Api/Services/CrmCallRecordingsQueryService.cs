using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>Office-scoped archive access, independent of the public location of audio files.</summary>
public sealed class CrmCallRecordingsQueryService(OrbitaDbContext db, CrmCallRecordingStorageService storage)
{
    public async Task<CrmCallRecordingsDto?> GetAsync(Guid? officeId, ClaimsPrincipal actor,
        DateTime fromUtc, DateTime toUtc, string? managerUserId = null, string? phone = null,
        string? direction = null, int page = 1, CancellationToken ct = default)
    {
        if (toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(366)
            || (phone?.Length ?? 0) > 64 || (managerUserId?.Length ?? 0) > 128)
            return null;
        if (!string.IsNullOrWhiteSpace(direction)
            && direction is not CrmCallDirections.Incoming and not CrmCallDirections.Outgoing)
            return null;
        var digits = new string((phone ?? "").Where(char.IsAsciiDigit).ToArray());
        if (!string.IsNullOrWhiteSpace(phone) && digits.Length < 3) return null;
        if (digits.Length == 11 && digits[0] == '8') digits = "7" + digits[1..];

        var calls = await AccessibleCallsAsync(officeId, actor, ct);
        if (calls is null) return null;
        calls = calls.Where(x => x.StartedAtUtc >= fromUtc && x.StartedAtUtc < toUtc);
        if (digits.Length > 0) calls = calls.Where(x => x.ClientPhoneNormalized.Contains(digits));
        if (!string.IsNullOrWhiteSpace(direction)) calls = calls.Where(x => x.Direction == direction);

        var query = from call in calls
                    join office in db.Offices on call.OfficeId equals office.Id
                    from card in db.CrmCandidateCards.Where(x => x.Id == call.CardId
                        && x.OfficeId == call.OfficeId).DefaultIfEmpty()
                    select new
                    {
                        Call = call, Card = card, OfficeName = office.Name,
                        // A known but unassigned card belongs to the queue, not its former caller.
                        ResponsibleId = card != null ? card.ManagerUserId : call.ManagerUserId
                    };
        var managerIds = await query.Where(x => x.ResponsibleId != null)
            .Select(x => x.ResponsibleId!).Distinct().ToArrayAsync(ct);
        var names = await db.PanelUserProfiles.AsNoTracking().Where(x => managerIds.Contains(x.UserId))
            .Select(x => new { x.UserId, x.FullName }).ToDictionaryAsync(x => x.UserId, x => x.FullName, ct);
        string Name(string id) => names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name : "Сотрудник";
        var managers = managerIds.Select(id => new CrmCallRecordingManagerDto(id, Name(id)))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToArray();
        if (!string.IsNullOrWhiteSpace(managerUserId)) query = query.Where(x => x.ResponsibleId == managerUserId);

        var total = await query.CountAsync(ct);
        const int pageSize = 30;
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var rows = await query.OrderByDescending(x => x.Call.StartedAtUtc).ThenByDescending(x => x.Call.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Call.Id, CardId = x.Card != null ? (Guid?)x.Card.Id : null,
                CandidateName = x.Card != null ? x.Card.Response.FullName : null,
                Phone = x.Call.ClientPhoneNormalized, x.Call.StartedAtUtc, x.Call.DurationSeconds,
                x.Call.Direction, x.ResponsibleId, x.OfficeName
            }).ToListAsync(ct);
        return new(total, page, pageSize, rows.Select(x => new CrmCallRecordingRowDto(
            x.Id, x.CardId, x.CandidateName, x.Phone, x.StartedAtUtc, x.DurationSeconds,
            x.Direction, x.ResponsibleId == null ? null : Name(x.ResponsibleId), x.OfficeName)).ToArray(), managers);
    }

    public async Task<(Stream? Stream, string? FileName, string? ContentType)> OpenAsync(
        Guid callId, ClaimsPrincipal actor, CancellationToken ct = default)
    {
        // Repeat the same scope checks on EVERY audio/download request; a guessed
        // call ID or a previously opened page must never grant cross-office access.
        var calls = await AccessibleCallsAsync(null, actor, ct);
        if (calls is null) return (null, null, null);
        var call = await calls.FirstOrDefaultAsync(x => x.Id == callId, ct);
        if (call is null) return (null, null, null);
        var contentType = string.IsNullOrWhiteSpace(call.RecordingContentType) ? "audio/wav" : call.RecordingContentType;
        return (storage.OpenRead(call.RecordingStoragePath!),
            string.IsNullOrWhiteSpace(call.RecordingFileName) ? $"Звонок-{call.Id:N}.wav" : call.RecordingFileName,
            contentType);
    }

    private async Task<IQueryable<CrmCallEntity>?> AccessibleCallsAsync(
        Guid? officeId, ClaimsPrincipal actor, CancellationToken ct)
    {
        var userId = actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub");
        if (actor.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId)
            || !PanelRoles.CanAccessCallRecordings(actor) || officeId == Guid.Empty)
            return null;
        if (!PanelRoles.IsGlobalAdmin(actor))
        {
            // Consult current DB assignment, not a potentially stale office claim.
            var ownOffice = await db.PanelUserProfiles.AsNoTracking().Where(x => x.UserId == userId)
                .Select(x => x.OfficeId).FirstOrDefaultAsync(ct);
            if (ownOffice is null || ownOffice == Guid.Empty || (officeId is not null && officeId != ownOffice))
                return null;
            officeId = ownOffice;
        }
        return db.CrmCalls.AsNoTracking().Where(call =>
            call.RecordingStoragePath != null && call.RecordingStoragePath != ""
            && (officeId == null || call.OfficeId == officeId)
            && db.Offices.Any(o => o.Id == call.OfficeId && o.IsEnabled && o.CrmEnabled)
            && (call.CardId == null || db.CrmCandidateCards.Any(c => c.Id == call.CardId && c.OfficeId == call.OfficeId)));
    }
}

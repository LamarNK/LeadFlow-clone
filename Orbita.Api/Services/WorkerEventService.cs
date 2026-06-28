using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;

namespace Orbita.Api.Services;

public sealed class WorkerEventService(OrbitaDbContext db, OfficeScopeService officeScope)
{
    public async Task<(bool Success, string? Error)> DismissAsync(
        Guid eventId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var evt = await db.WorkerEvents.FirstOrDefaultAsync(x => x.Id == eventId, ct);
        if (evt is null)
        {
            return (false, "Событие не найдено.");
        }

        if (!await officeScope.CanAccessWorkerAsync(scope, evt.WorkerId, ct))
        {
            return (false, "Нет доступа к событию.");
        }

        if (evt.IsDismissed)
        {
            return (true, null);
        }

        evt.IsDismissed = true;
        evt.DismissedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }
}
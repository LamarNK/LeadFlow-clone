using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class OfficeScopeService(OrbitaDbContext db)
{
    public OfficeScope Resolve(ClaimsPrincipal principal)
    {
        if (principal.IsInRole(PanelRoles.Admin))
        {
            return OfficeScope.GlobalAdmin;
        }

        if (Guid.TryParse(principal.FindFirstValue(OfficeClaims.OfficeId), out var officeId))
        {
            return OfficeScope.ForOffice(officeId);
        }

        return OfficeScope.NoAccess;
    }

    public async Task<OfficeScope> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (principal.IsInRole(PanelRoles.Admin))
        {
            return OfficeScope.GlobalAdmin;
        }

        if (Guid.TryParse(principal.FindFirstValue(OfficeClaims.OfficeId), out var officeId))
        {
            return OfficeScope.ForOffice(officeId);
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return OfficeScope.NoAccess;
        }

        var profileOfficeId = await db.PanelUserProfiles
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.OfficeId)
            .FirstOrDefaultAsync(ct);

        return profileOfficeId is Guid resolvedOfficeId
            ? OfficeScope.ForOffice(resolvedOfficeId)
            : OfficeScope.NoAccess;
    }

    public IQueryable<WorkerEntity> ApplyWorkerFilter(IQueryable<WorkerEntity> query, OfficeScope scope, Guid? officeFilter = null)
    {
        var effectiveOfficeId = scope.ResolveFilter(officeFilter);
        if (effectiveOfficeId is Guid officeId)
        {
            return query.Where(x => x.OfficeId == officeId);
        }

        if (!scope.IsGlobalAdmin)
        {
            return query.Where(_ => false);
        }

        return query;
    }

    public async Task<bool> CanAccessWorkerAsync(OfficeScope scope, Guid workerId, CancellationToken ct = default)
    {
        if (!scope.HasAccess)
        {
            return false;
        }

        if (scope.IsGlobalAdmin)
        {
            return await db.Workers.AsNoTracking().AnyAsync(x => x.Id == workerId, ct);
        }

        return await db.Workers.AsNoTracking()
            .AnyAsync(x => x.Id == workerId && x.OfficeId == scope.OfficeId, ct);
    }

    public async Task<Guid?> GetWorkerOfficeIdAsync(Guid workerId, CancellationToken ct = default) =>
        await db.Workers.AsNoTracking()
            .Where(x => x.Id == workerId)
            .Select(x => (Guid?)x.OfficeId)
            .FirstOrDefaultAsync(ct);
}
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class OfficeBitrixSettingsService(OrbitaDbContext db, PanelAuditService audit)
{
    public async Task<OfficeBitrixSettingsDto?> GetForScopeAsync(
        OfficeScope scope,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        var (targetOfficeId, _) = OfficeIdResolver.Resolve(scope, officeId);
        if (targetOfficeId is not Guid resolvedOfficeId)
        {
            return null;
        }

        var office = await db.Offices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == resolvedOfficeId, ct);
        if (office is null)
        {
            return null;
        }

        var transmissionEnabled = await ResolveTransmissionEnabledAsync(resolvedOfficeId, office.BitrixTransmissionEnabled, ct);
        return new OfficeBitrixSettingsDto(office.Id, office.Name, transmissionEnabled);
    }

    public async Task<(OfficeBitrixSettingsDto? Settings, string? Error)> UpdateForScopeAsync(
        OfficeScope scope,
        bool transmissionEnabled,
        string actorUserId,
        string? actorEmail,
        string? ipAddress,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        var (targetOfficeId, error) = OfficeIdResolver.Resolve(scope, officeId);
        if (targetOfficeId is not Guid resolvedOfficeId)
        {
            return (null, error ?? "Нет доступа к офису.");
        }

        var office = await db.Offices.FindAsync([resolvedOfficeId], ct);
        if (office is null)
        {
            return (null, "Офис не найден.");
        }

        await SetTransmissionEnabledAsync(office, transmissionEnabled, actorUserId, ct);

        await audit.LogAsync(
            actorUserId,
            actorEmail,
            PanelAuditOfficeActions.BitrixTransmissionUpdated,
            "office",
            office.Id.ToString(),
            transmissionEnabled ? "enabled" : "disabled",
            ipAddress,
            ct);

        return (new OfficeBitrixSettingsDto(office.Id, office.Name, transmissionEnabled), null);
    }

    public async Task<bool> IsTransmissionEnabledAsync(Guid officeId, CancellationToken ct = default)
    {
        var office = await db.Offices.AsNoTracking()
            .Where(x => x.Id == officeId)
            .Select(x => new { x.BitrixTransmissionEnabled })
            .FirstOrDefaultAsync(ct);

        if (office is null)
        {
            return true;
        }

        return await ResolveTransmissionEnabledAsync(officeId, office.BitrixTransmissionEnabled, ct);
    }

    public async Task SetTransmissionEnabledAsync(
        OfficeEntity office,
        bool transmissionEnabled,
        string actorUserId,
        CancellationToken ct = default)
    {
        office.BitrixTransmissionEnabled = transmissionEnabled;

        var route = await db.DistributionRoutes.FirstOrDefaultAsync(x => x.OfficeId == office.Id, ct);
        var now = DateTime.UtcNow;
        if (route is null)
        {
            route = new DistributionRouteEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = office.Id,
                IsAutoDistributionEnabled = transmissionEnabled,
                UpdatedAtUtc = now,
                UpdatedByUserId = actorUserId
            };
            db.DistributionRoutes.Add(route);
        }
        else
        {
            route.IsAutoDistributionEnabled = transmissionEnabled;
            route.UpdatedAtUtc = now;
            route.UpdatedByUserId = actorUserId;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> ResolveTransmissionEnabledAsync(
        Guid officeId,
        bool officeFallback,
        CancellationToken ct)
    {
        var route = await db.DistributionRoutes
            .AsNoTracking()
            .Where(x => x.OfficeId == officeId)
            .Select(x => new { x.IsAutoDistributionEnabled })
            .FirstOrDefaultAsync(ct);

        return route?.IsAutoDistributionEnabled ?? officeFallback;
    }
}
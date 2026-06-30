using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class OfficeBitrixSettingsService(OrbitaDbContext db, PanelAuditService audit)
{
    public async Task<OfficeBitrixSettingsDto?> GetForScopeAsync(OfficeScope scope, CancellationToken ct = default)
    {
        if (!scope.HasAccess || scope.OfficeId is not Guid officeId)
        {
            return null;
        }

        var office = await db.Offices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == officeId, ct);
        return office is null
            ? null
            : new OfficeBitrixSettingsDto(office.Id, office.Name, office.BitrixTransmissionEnabled);
    }

    public async Task<(OfficeBitrixSettingsDto? Settings, string? Error)> UpdateForScopeAsync(
        OfficeScope scope,
        bool transmissionEnabled,
        string actorUserId,
        string? actorEmail,
        string? ipAddress,
        CancellationToken ct = default)
    {
        if (!scope.HasAccess || scope.OfficeId is not Guid officeId)
        {
            return (null, "Нет доступа к офису.");
        }

        var office = await db.Offices.FindAsync([officeId], ct);
        if (office is null)
        {
            return (null, "Офис не найден.");
        }

        office.BitrixTransmissionEnabled = transmissionEnabled;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            actorUserId,
            actorEmail,
            PanelAuditOfficeActions.BitrixTransmissionUpdated,
            "office",
            office.Id.ToString(),
            transmissionEnabled ? "enabled" : "disabled",
            ipAddress,
            ct);

        return (new OfficeBitrixSettingsDto(office.Id, office.Name, office.BitrixTransmissionEnabled), null);
    }

    public async Task<bool> IsTransmissionEnabledAsync(Guid officeId, CancellationToken ct = default)
    {
        var office = await db.Offices.AsNoTracking()
            .Where(x => x.Id == officeId)
            .Select(x => new { x.BitrixTransmissionEnabled })
            .FirstOrDefaultAsync(ct);

        return office?.BitrixTransmissionEnabled ?? true;
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;

namespace Orbita.Api.Services;

public sealed class BitrixLegacyMigrationService(
    OrbitaDbContext db,
    IOptions<OrbitaBitrixSettings> defaultBitrixOptions)
{
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var offices = await db.Offices
            .Where(x => x.BitrixWebhookUrlProtected != null && x.BitrixWebhookUrlProtected != "")
            .ToListAsync(ct);

        foreach (var office in offices)
        {
            var hasInstances = await db.BitrixInstances.AnyAsync(x => x.OfficeId == office.Id, ct);
            if (hasInstances)
            {
                await EnsureDistributionRouteAsync(office, ct);
                continue;
            }

            var now = DateTime.UtcNow;
            var integration = BitrixInstanceIntegrationSettings.FromDefaults(defaultBitrixOptions.Value);
            var instance = new BitrixInstanceEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = office.Id,
                Name = office.Name,
                Signature = string.Empty,
                WebhookUrlProtected = office.BitrixWebhookUrlProtected!,
                PortalHost = office.BitrixPortalHost,
                ValidationStatus = office.BitrixValidationStatus,
                ValidationMessage = office.BitrixValidationMessage,
                LastValidatedAtUtc = office.BitrixLastValidatedAtUtc,
                IntegrationSettingsJson = integration.Serialize(),
                IsEnabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                UpdatedByUserId = office.BitrixUpdatedByUserId
            };
            db.BitrixInstances.Add(instance);

            var route = await db.DistributionRoutes
                .Include(x => x.Nodes)
                .FirstOrDefaultAsync(x => x.OfficeId == office.Id, ct);

            if (route is null)
            {
                route = new DistributionRouteEntity
                {
                    Id = Guid.NewGuid(),
                    OfficeId = office.Id,
                    IsAutoDistributionEnabled = office.BitrixTransmissionEnabled,
                    UpdatedAtUtc = now,
                    UpdatedByUserId = "legacy-migration"
                };
                db.DistributionRoutes.Add(route);
            }
            else
            {
                route.IsAutoDistributionEnabled = office.BitrixTransmissionEnabled;
                route.UpdatedAtUtc = now;
                route.UpdatedByUserId = "legacy-migration";
            }

            if (route.Nodes.Count == 0)
            {
                db.DistributionNodes.Add(new DistributionNodeEntity
                {
                    Id = Guid.NewGuid(),
                    RouteId = route.Id,
                    BitrixInstanceId = instance.Id,
                    SortOrder = 0,
                    EditorPositionX = 120,
                    EditorPositionY = 120
                });
            }

            await db.SaveChangesAsync(ct);
        }

        var routesWithoutInstances = await db.Offices
            .Where(x => !db.BitrixInstances.Any(b => b.OfficeId == x.Id))
            .Where(x => !db.DistributionRoutes.Any(r => r.OfficeId == x.Id))
            .Select(x => x.Id)
            .ToListAsync(ct);

        foreach (var officeId in routesWithoutInstances)
        {
            var office = await db.Offices.AsNoTracking().FirstAsync(x => x.Id == officeId, ct);
            db.DistributionRoutes.Add(new DistributionRouteEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = officeId,
                IsAutoDistributionEnabled = office.BitrixTransmissionEnabled,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedByUserId = "legacy-migration"
            });
        }

        if (routesWithoutInstances.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task EnsureDistributionRouteAsync(OfficeEntity office, CancellationToken ct)
    {
        var route = await db.DistributionRoutes.FirstOrDefaultAsync(x => x.OfficeId == office.Id, ct);
        if (route is not null)
        {
            return;
        }

        db.DistributionRoutes.Add(new DistributionRouteEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = office.Id,
            IsAutoDistributionEnabled = office.BitrixTransmissionEnabled,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = "legacy-migration"
        });
        await db.SaveChangesAsync(ct);
    }
}
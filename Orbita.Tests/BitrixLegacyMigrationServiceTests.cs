using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixLegacyMigrationServiceTests
{
    [Fact]
    public async Task MigrateAsync_CreatesInstanceAndRouteFromOfficeWebhook()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Legacy Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            BitrixTransmissionEnabled = true,
            BitrixWebhookUrlProtected = "protected-webhook",
            BitrixPortalHost = "legacy.bitrix24.ru",
            BitrixValidationStatus = BitrixValidationStatuses.Ok
        });
        await db.SaveChangesAsync();

        var sut = new BitrixLegacyMigrationService(db, Options.Create(new OrbitaBitrixSettings()));
        await sut.MigrateAsync();

        var instance = await db.BitrixInstances.SingleAsync(x => x.OfficeId == officeId);
        Assert.Equal("Legacy Office", instance.Name);
        Assert.Equal("protected-webhook", instance.WebhookUrlProtected);

        var route = await db.DistributionRoutes.SingleAsync(x => x.OfficeId == officeId);
        Assert.True(route.IsAutoDistributionEnabled);
        var node = await db.DistributionNodes.SingleAsync(x => x.RouteId == route.Id);
        Assert.Equal(instance.Id, node.BitrixInstanceId);
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }
}
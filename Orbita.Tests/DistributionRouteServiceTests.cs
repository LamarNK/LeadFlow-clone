using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class DistributionRouteServiceTests
{
    [Fact]
    public async Task SaveAsync_RejectsCycle()
    {
        await using var db = CreateDb();
        var officeId = SeedOffice(db);
        var bitrix1 = SeedBitrix(db, officeId, "B1");
        var bitrix2 = SeedBitrix(db, officeId, "B2");
        await db.SaveChangesAsync();

        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        var sut = new DistributionRouteService(db, new PanelAuditService(db));
        var (_, error) = await sut.SaveAsync(
            OfficeScope.ForOffice(officeId),
            officeId,
            new SaveDistributionRouteRequest(
                true,
                [
                    new SaveDistributionNodeRequest(nodeA, nodeB, bitrix1, 0, 10, 10),
                    new SaveDistributionNodeRequest(nodeB, nodeA, bitrix2, 1, 20, 20)
                ]),
            "tester");

        Assert.Equal("Схема связей содержит цикл.", error);
    }

    [Fact]
    public async Task SaveAsync_AssignsStableIdsForNewNodes()
    {
        await using var db = CreateDb();
        var officeId = SeedOffice(db);
        var bitrix1 = SeedBitrix(db, officeId, "B1");
        await db.SaveChangesAsync();

        var sut = new DistributionRouteService(db, new PanelAuditService(db));
        var (route, error) = await sut.SaveAsync(
            OfficeScope.ForOffice(officeId),
            officeId,
            new SaveDistributionRouteRequest(
                true,
                [new SaveDistributionNodeRequest(null, null, bitrix1, 0, 10, 10)]),
            "tester");

        Assert.Null(error);
        Assert.NotNull(route);
        Assert.Single(route!.Nodes);
        Assert.NotEqual(Guid.Empty, route.Nodes[0].Id);
    }

    [Fact]
    public async Task SaveAsync_ClearsRoundRobinState()
    {
        await using var db = CreateDb();
        var officeId = SeedOffice(db);
        var bitrix1 = SeedBitrix(db, officeId, "B1");
        var bitrix2 = SeedBitrix(db, officeId, "B2");
        var routeId = Guid.NewGuid();
        db.DistributionRoutes.Add(new DistributionRouteEntity
        {
            Id = routeId,
            OfficeId = officeId,
            IsAutoDistributionEnabled = true,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.DistributionNodes.Add(new DistributionNodeEntity
        {
            Id = Guid.NewGuid(),
            RouteId = routeId,
            BitrixInstanceId = bitrix1,
            SortOrder = 0
        });
        db.DistributionRoundRobinStates.Add(new DistributionRoundRobinStateEntity
        {
            RouteId = routeId,
            NextChildIndex = 3
        });
        await db.SaveChangesAsync();

        var sut = new DistributionRouteService(db, new PanelAuditService(db));
        var (_, error) = await sut.SaveAsync(
            OfficeScope.ForOffice(officeId),
            officeId,
            new SaveDistributionRouteRequest(
                true,
                [new SaveDistributionNodeRequest(null, null, bitrix2, 0, 10, 10)]),
            "tester");

        Assert.Null(error);
        Assert.Empty(await db.DistributionRoundRobinStates.Where(x => x.RouteId == routeId).ToListAsync());
    }

    private static Guid SeedOffice(OrbitaDbContext db)
    {
        var officeId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        return officeId;
    }

    private static Guid SeedBitrix(OrbitaDbContext db, Guid officeId, string name)
    {
        var id = Guid.NewGuid();
        db.BitrixInstances.Add(new BitrixInstanceEntity
        {
            Id = id,
            OfficeId = officeId,
            Name = name,
            Signature = name,
            WebhookUrlProtected = "protected",
            ValidationStatus = BitrixValidationStatuses.Ok,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        return id;
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }
}
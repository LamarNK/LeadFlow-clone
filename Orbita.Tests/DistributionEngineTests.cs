using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class DistributionEngineTests
{
    [Fact]
    public async Task GetPlanAsync_FlatTree_UsesBroadcastTopology()
    {
        var officeId = Guid.NewGuid();
        await using var db = CreateDb();
        var bitrix1 = Guid.NewGuid();
        var bitrix2 = Guid.NewGuid();
        var bitrix3 = Guid.NewGuid();
        SeedOfficeWithBitrix(db, officeId, bitrix1, bitrix2, bitrix3);

        var routeId = Guid.NewGuid();
        db.DistributionRoutes.Add(new DistributionRouteEntity
        {
            Id = routeId,
            OfficeId = officeId,
            IsAutoDistributionEnabled = true,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.DistributionNodes.AddRange(
            new DistributionNodeEntity { Id = Guid.NewGuid(), RouteId = routeId, BitrixInstanceId = bitrix1, SortOrder = 0 },
            new DistributionNodeEntity { Id = Guid.NewGuid(), RouteId = routeId, BitrixInstanceId = bitrix2, SortOrder = 1 },
            new DistributionNodeEntity { Id = Guid.NewGuid(), RouteId = routeId, BitrixInstanceId = bitrix3, SortOrder = 2 });
        await db.SaveChangesAsync();

        var plan = await new DistributionEngine(db).GetPlanAsync(officeId);

        Assert.Null(plan.Error);
        Assert.Equal(DistributionTopology.Broadcast, plan.Topology);
        Assert.Equal([bitrix1, bitrix2, bitrix3], plan.Targets.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task GetPlanAsync_LinearChain_UsesChainFallbackTopology()
    {
        var officeId = Guid.NewGuid();
        await using var db = CreateDb();
        var bitrix1 = Guid.NewGuid();
        var bitrix2 = Guid.NewGuid();
        var bitrix3 = Guid.NewGuid();
        SeedOfficeWithBitrix(db, officeId, bitrix1, bitrix2, bitrix3);

        var routeId = Guid.NewGuid();
        var node1 = Guid.NewGuid();
        var node2 = Guid.NewGuid();
        var node3 = Guid.NewGuid();
        db.DistributionRoutes.Add(new DistributionRouteEntity
        {
            Id = routeId,
            OfficeId = officeId,
            IsAutoDistributionEnabled = true,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.DistributionNodes.AddRange(
            new DistributionNodeEntity { Id = node1, RouteId = routeId, BitrixInstanceId = bitrix1, SortOrder = 0 },
            new DistributionNodeEntity { Id = node2, RouteId = routeId, ParentNodeId = node1, BitrixInstanceId = bitrix2, SortOrder = 1 },
            new DistributionNodeEntity { Id = node3, RouteId = routeId, ParentNodeId = node2, BitrixInstanceId = bitrix3, SortOrder = 2 });
        await db.SaveChangesAsync();

        var plan = await new DistributionEngine(db).GetPlanAsync(officeId);

        Assert.Null(plan.Error);
        Assert.Equal(DistributionTopology.ChainFallback, plan.Topology);
        Assert.Equal([bitrix1, bitrix2, bitrix3], plan.Targets.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task GetPlanAsync_BranchingTree_ReturnsUnsupportedError()
    {
        var officeId = Guid.NewGuid();
        await using var db = CreateDb();
        var bitrix1 = Guid.NewGuid();
        var bitrix2 = Guid.NewGuid();
        var bitrix3 = Guid.NewGuid();
        SeedOfficeWithBitrix(db, officeId, bitrix1, bitrix2, bitrix3);

        var routeId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        db.DistributionRoutes.Add(new DistributionRouteEntity
        {
            Id = routeId,
            OfficeId = officeId,
            IsAutoDistributionEnabled = true,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.DistributionNodes.AddRange(
            new DistributionNodeEntity { Id = rootId, RouteId = routeId, BitrixInstanceId = bitrix1, SortOrder = 0 },
            new DistributionNodeEntity { Id = Guid.NewGuid(), RouteId = routeId, ParentNodeId = rootId, BitrixInstanceId = bitrix2, SortOrder = 1 },
            new DistributionNodeEntity { Id = Guid.NewGuid(), RouteId = routeId, ParentNodeId = rootId, BitrixInstanceId = bitrix3, SortOrder = 2 });
        await db.SaveChangesAsync();

        var plan = await new DistributionEngine(db).GetPlanAsync(officeId);

        Assert.NotNull(plan.Error);
        Assert.Empty(plan.Targets);
    }

    private static void SeedOfficeWithBitrix(OrbitaDbContext db, Guid officeId, params Guid[] bitrixIds)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Test",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.BitrixInstances.AddRange(bitrixIds.Select((id, index) => CreateBitrix(id, officeId, $"B{index + 1}")));
    }

    private static BitrixInstanceEntity CreateBitrix(Guid id, Guid officeId, string name) => new()
    {
        Id = id,
        OfficeId = officeId,
        Name = name,
        Signature = name,
        WebhookUrlProtected = "protected",
        ValidationStatus = BitrixValidationStatuses.Ok,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow
    };

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }
}
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class LeadExportQuotaServiceTests
{
    [Fact]
    public async Task CanExportToBitrixAsync_ReturnsFalse_WhenSessionCountReachedLimit()
    {
        await using var db = CreateDb();
        var bitrixId = Guid.NewGuid();
        db.BitrixInstances.Add(CreateBitrix(bitrixId, limit: 40, count: 40));
        await db.SaveChangesAsync();

        var service = new LeadExportQuotaService(db);
        Assert.False(await service.CanExportToBitrixAsync(bitrixId));
    }

    [Fact]
    public async Task RecordSuccessfulExportAsync_IncrementsSessionCount()
    {
        await using var db = CreateDb();
        var bitrixId = Guid.NewGuid();
        db.BitrixInstances.Add(CreateBitrix(bitrixId, limit: 40, count: 39));
        await db.SaveChangesAsync();

        var service = new LeadExportQuotaService(db);
        Assert.True(await service.CanExportToBitrixAsync(bitrixId));

        await service.RecordSuccessfulExportAsync(bitrixId);

        var instance = await db.BitrixInstances.SingleAsync(x => x.Id == bitrixId);
        Assert.Equal(40, instance.LeadExportSessionCount);
        Assert.False(await service.CanExportToBitrixAsync(bitrixId));
    }

    [Fact]
    public async Task BitrixInstancesHaveIndependentSessionCounters()
    {
        await using var db = CreateDb();
        var bitrixA = Guid.NewGuid();
        var bitrixB = Guid.NewGuid();
        db.BitrixInstances.AddRange(
            CreateBitrix(bitrixA, limit: 40, count: 39),
            CreateBitrix(bitrixB, limit: 40, count: 0));
        await db.SaveChangesAsync();

        var service = new LeadExportQuotaService(db);
        await service.RecordSuccessfulExportAsync(bitrixA);

        var instanceA = await db.BitrixInstances.SingleAsync(x => x.Id == bitrixA);
        var instanceB = await db.BitrixInstances.SingleAsync(x => x.Id == bitrixB);
        Assert.Equal(40, instanceA.LeadExportSessionCount);
        Assert.Equal(0, instanceB.LeadExportSessionCount);
        Assert.False(await service.CanExportToBitrixAsync(bitrixA));
        Assert.True(await service.CanExportToBitrixAsync(bitrixB));
    }

    [Fact]
    public async Task ResetSessionsForOfficeAsync_ClearsSessionCountForOfficeBitrixes()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var bitrixId = Guid.NewGuid();
        db.BitrixInstances.Add(CreateBitrix(bitrixId, limit: 40, count: 40, officeId: officeId));
        await db.SaveChangesAsync();

        var service = new LeadExportQuotaService(db);
        await service.ResetSessionsForOfficeAsync(officeId);

        var instance = await db.BitrixInstances.SingleAsync(x => x.Id == bitrixId);
        Assert.Equal(0, instance.LeadExportSessionCount);
        Assert.NotNull(instance.LeadExportSessionStartedAtUtc);
        Assert.True(await service.CanExportToBitrixAsync(bitrixId));
    }

    private static BitrixInstanceEntity CreateBitrix(
        Guid id,
        int? limit,
        int count,
        Guid? officeId = null) =>
        new()
        {
            Id = id,
            OfficeId = officeId ?? Guid.NewGuid(),
            Name = "Bitrix",
            Signature = "B1",
            WebhookUrlProtected = "protected",
            ValidationStatus = BitrixValidationStatuses.Ok,
            IsEnabled = true,
            LeadExportLimit = limit,
            LeadExportSessionCount = count,
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
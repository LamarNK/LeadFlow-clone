using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class OfficeScopeServiceTests
{
    [Fact]
    public void GlobalAdmin_CanAccessAnyOffice()
    {
        var scope = OfficeScope.GlobalAdmin;
        Assert.True(scope.CanAccessOffice(Guid.NewGuid()));
        Assert.Null(scope.ResolveFilter(null));
        Assert.Equal(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), scope.ResolveFilter(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
    }

    [Fact]
    public void OfficeOperator_ResolvesToOwnOffice()
    {
        var officeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var scope = OfficeScope.ForOffice(officeId);

        Assert.Equal(officeId, scope.ResolveFilter(null));
        Assert.Equal(officeId, scope.ResolveFilter(Guid.NewGuid()));
        Assert.True(scope.CanAccessOffice(officeId));
        Assert.False(scope.CanAccessOffice(Guid.NewGuid()));
    }

    [Fact]
    public async Task ApplyWorkerFilter_LimitsOperatorToOwnWorkers()
    {
        var officeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var officeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        await using var db = CreateDb();
        db.Offices.AddRange(
            new OfficeEntity { Id = officeA, Name = "A", RegistrationSecretHash = "hash-a", CreatedAtUtc = DateTime.UtcNow },
            new OfficeEntity { Id = officeB, Name = "B", RegistrationSecretHash = "hash-b", CreatedAtUtc = DateTime.UtcNow });
        db.Workers.AddRange(
            new WorkerEntity { Id = Guid.NewGuid(), OfficeId = officeA, DisplayName = "W1", ApiKeyHash = "h1", CreatedAtUtc = DateTime.UtcNow },
            new WorkerEntity { Id = Guid.NewGuid(), OfficeId = officeB, DisplayName = "W2", ApiKeyHash = "h2", CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var service = new OfficeScopeService(db);
        var operatorScope = OfficeScope.ForOffice(officeA);
        var workerIds = await service
            .ApplyWorkerFilter(db.Workers.AsNoTracking(), operatorScope)
            .Select(x => x.OfficeId)
            .ToListAsync();

        Assert.Single(workerIds);
        Assert.All(workerIds, id => Assert.Equal(officeA, id));
    }

    [Fact]
    public async Task CanAccessWorkerAsync_ReturnsFalseForCrossOffice()
    {
        var officeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var officeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var workerBId = Guid.NewGuid();
        await using var db = CreateDb();
        db.Offices.AddRange(
            new OfficeEntity { Id = officeA, Name = "A", RegistrationSecretHash = "hash-a", CreatedAtUtc = DateTime.UtcNow },
            new OfficeEntity { Id = officeB, Name = "B", RegistrationSecretHash = "hash-b", CreatedAtUtc = DateTime.UtcNow });
        db.Workers.Add(new WorkerEntity
        {
            Id = workerBId,
            OfficeId = officeB,
            DisplayName = "W2",
            ApiKeyHash = "h2",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = new OfficeScopeService(db);
        var canAccess = await service.CanAccessWorkerAsync(OfficeScope.ForOffice(officeA), workerBId);
        Assert.False(canAccess);
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }
}
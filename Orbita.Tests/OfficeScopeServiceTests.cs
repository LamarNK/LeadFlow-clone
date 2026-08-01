using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;
using System.Security.Claims;

namespace Orbita.Tests;

public sealed class OfficeScopeServiceTests
{
    [Fact]
    public void GlobalAdmin_HasAccessToAnyOfficeFilter()
    {
        var scope = OfficeScope.GlobalAdmin;
        Assert.True(scope.HasAccess);
        Assert.Null(scope.ResolveFilter(null));
        Assert.True(scope.CanAccessOffice(Guid.NewGuid()));
    }

    [Fact]
    public void OfficeOperator_ResolvesToOwnOffice()
    {
        var officeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var scope = OfficeScope.ForOffice(officeId);

        Assert.True(scope.HasAccess);
        Assert.Equal(officeId, scope.ResolveFilter(Guid.NewGuid()));
        Assert.True(scope.CanAccessOffice(officeId));
        Assert.False(scope.CanAccessOffice(Guid.NewGuid()));
        Assert.True(scope.CanAccessWorker(officeId));
        Assert.True(scope.CanAccessResponse(officeId));
        Assert.True(scope.CanAccessResponse(null, officeId));
        Assert.False(scope.CanAccessResponse(null, Guid.NewGuid()));
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

    [Fact]
    public void Resolve_OperatorRole_UsesOfficeClaim()
    {
        var officeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var claims = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "op-user"),
            new Claim(ClaimTypes.Role, PanelRoles.Operator),
            new Claim(OfficeClaims.OfficeId, officeId.ToString("D"))
        ], "test"));

        var service = new OfficeScopeService(CreateDb());
        var scope = service.Resolve(claims);
        Assert.Equal(officeId, scope.OfficeId);
        Assert.False(scope.IsGlobalAdmin);
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }
}

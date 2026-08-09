using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class OfficeAdminServiceTests
{
    [Fact]
    public async Task DeleteAsync_RemovesEmptyOffice()
    {
        await using var db = CreateDb();
        var keep = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var remove = Guid.Parse("22222222-2222-2222-2222-222222222222");
        db.Offices.AddRange(
            new OfficeEntity { Id = keep, Name = "Keep", RegistrationSecretHash = "h1", CreatedAtUtc = DateTime.UtcNow },
            new OfficeEntity { Id = remove, Name = "Remove", RegistrationSecretHash = "h2", CreatedAtUtc = DateTime.UtcNow });
        db.PanelUserProfiles.Add(new PanelUserProfileEntity
        {
            UserId = "user-1",
            FullName = "Operator",
            OfficeId = remove
        });
        await db.SaveChangesAsync();

        var sut = new OfficeAdminService(db);
        var (success, error, name) = await sut.DeleteAsync(remove);

        Assert.True(success, error);
        Assert.Equal("Remove", name);
        Assert.Null(error);
        Assert.False(await db.Offices.AnyAsync(x => x.Id == remove));
        Assert.True(await db.Offices.AnyAsync(x => x.Id == keep));
        Assert.Null(await db.PanelUserProfiles.Where(x => x.UserId == "user-1").Select(x => x.OfficeId).SingleAsync());
    }

    [Fact]
    public async Task DeleteAsync_BlocksWhenWorkersExist()
    {
        await using var db = CreateDb();
        var keep = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var remove = Guid.Parse("22222222-2222-2222-2222-222222222222");
        db.Offices.AddRange(
            new OfficeEntity { Id = keep, Name = "Keep", RegistrationSecretHash = "h1", CreatedAtUtc = DateTime.UtcNow },
            new OfficeEntity { Id = remove, Name = "Busy", RegistrationSecretHash = "h2", CreatedAtUtc = DateTime.UtcNow });
        db.Workers.Add(new WorkerEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = remove,
            DisplayName = "W1",
            ApiKeyHash = "hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = new OfficeAdminService(db);
        var (success, error, name) = await sut.DeleteAsync(remove);

        Assert.False(success);
        Assert.Null(name);
        Assert.Contains("воркер", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.Offices.AnyAsync(x => x.Id == remove));
    }

    [Fact]
    public async Task DeleteAsync_BlocksLastOffice()
    {
        await using var db = CreateDb();
        var only = Guid.Parse("11111111-1111-1111-1111-111111111111");
        db.Offices.Add(new OfficeEntity
        {
            Id = only,
            Name = "Only",
            RegistrationSecretHash = "h1",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = new OfficeAdminService(db);
        var (success, error, name) = await sut.DeleteAsync(only);

        Assert.False(success);
        Assert.Null(name);
        Assert.Contains("последний", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.Offices.AnyAsync(x => x.Id == only));
    }

    [Fact]
    public async Task DeleteAsync_CleansBitrixAndCrmOwnedData()
    {
        await using var db = CreateDb();
        var keep = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var remove = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var bitrixId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var routeId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var responseId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var personId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var cardId = Guid.Parse("77777777-7777-7777-7777-777777777777");

        db.Offices.AddRange(
            new OfficeEntity { Id = keep, Name = "Keep", RegistrationSecretHash = "h1", CreatedAtUtc = DateTime.UtcNow },
            new OfficeEntity { Id = remove, Name = "Remove", RegistrationSecretHash = "h2", CreatedAtUtc = DateTime.UtcNow });
        db.BitrixInstances.Add(new BitrixInstanceEntity
        {
            Id = bitrixId,
            OfficeId = remove,
            Name = "BX",
            Signature = "sig",
            WebhookUrlProtected = "url",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.DistributionRoutes.Add(new DistributionRouteEntity
        {
            Id = routeId,
            OfficeId = remove,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.DistributionNodes.Add(new DistributionNodeEntity
        {
            Id = Guid.NewGuid(),
            RouteId = routeId,
            BitrixInstanceId = bitrixId,
            SortOrder = 0
        });
        db.CandidatePersons.Add(new CandidatePersonEntity
        {
            Id = personId,
            OfficeId = remove,
            FullName = "Person"
        });
        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = responseId,
            PersonId = personId,
            OfficeId = remove,
            FullName = "Person",
            Status = "new",
            CreatedAt = DateTime.UtcNow,
            CollectedAt = DateTime.UtcNow
        });
        db.CrmCandidateCards.Add(new CrmCandidateCardEntity
        {
            Id = cardId,
            ResponseId = responseId,
            OfficeId = remove,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            StageChangedAtUtc = DateTime.UtcNow
        });
        db.ResponseCrmDeliveries.Add(new ResponseCrmDeliveryEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = responseId,
            OfficeId = remove,
            CardId = cardId,
            Outcome = "ok",
            Source = "manual",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = new OfficeAdminService(db);
        var (success, error, _) = await sut.DeleteAsync(remove);

        Assert.True(success, error);
        Assert.False(await db.Offices.AnyAsync(x => x.Id == remove));
        Assert.False(await db.BitrixInstances.AnyAsync(x => x.Id == bitrixId));
        Assert.False(await db.DistributionRoutes.AnyAsync(x => x.Id == routeId));
        Assert.False(await db.CrmCandidateCards.AnyAsync(x => x.Id == cardId));
        Assert.False(await db.ResponseCrmDeliveries.AnyAsync(x => x.OfficeId == remove));
        // Responses stay but lose office link (SetNull).
        Assert.Null(await db.CandidateResponses.Where(x => x.Id == responseId).Select(x => x.OfficeId).SingleAsync());
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }
}

using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CandidateLookupServiceTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task LookupAsync_BatchPhonesWithSubProfile_ReturnsOnlyMatchingSubProfile()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        db.CandidateResponses.AddRange(
            NewResponse("79001111111", "sub-a"),
            NewResponse("79002222222", "sub-b"));
        await db.SaveChangesAsync();

        var sut = new CandidateLookupService(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "PerAvitoAccount",
                [],
                ["79001111111", "79002222222"],
                AvitoSubProfileId: "sub-a"));

        Assert.NotNull(result);
        Assert.Equal(["79001111111"], result!.ExistingPhones);
    }

    [Fact]
    public async Task LookupAsync_BatchPhonesWithoutSubProfile_UsesDuplicateScope()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        var otherAccountId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        db.CandidateResponses.AddRange(
            NewResponse("79003333333", "sub-a", AccountId),
            NewResponse("79003333333", "sub-b", otherAccountId));
        await db.SaveChangesAsync();

        var sut = new CandidateLookupService(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "PerAvitoAccount",
                [],
                ["79003333333"]));

        Assert.NotNull(result);
        Assert.Equal(["79003333333"], result!.ExistingPhones);
    }

    [Fact]
    public async Task LookupAsync_LegacyEmptySubProfile_DoesNotMatchSubProfileFilter()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        db.CandidateResponses.Add(NewResponse("79004444444", ""));
        await db.SaveChangesAsync();

        var sut = new CandidateLookupService(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "PerAvitoAccount",
                [],
                ["79004444444"],
                AvitoSubProfileId: "sub-a"));

        Assert.NotNull(result);
        Assert.Empty(result!.ExistingPhones);
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedWorker(OrbitaDbContext db)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "Test Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerId,
            OfficeId = OfficeId,
            DisplayName = "worker-1",
            MachineName = "pc",
            ApiKeyHash = "hash",
            AppVersion = "1.0",
            MonitoringStatus = "Stopped",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static CandidateResponseEntity NewResponse(
        string phone,
        string subProfileId,
        Guid? accountId = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            WorkerId = WorkerId,
            AccountId = accountId ?? AccountId,
            AccountName = "acc",
            Source = "Avito",
            SourceResponseId = Guid.NewGuid().ToString("N"),
            FullName = "User",
            PhoneRaw = phone,
            PhoneNormalized = phone,
            AvitoSubProfileId = subProfileId,
            Status = ResponseStatuses.Sent,
            CreatedAt = DateTime.UtcNow
        };
}
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;
using CandidateParser = Orbita.Api.Services.CandidateParser;

namespace Orbita.Tests;

public sealed class CandidateDuplicateServiceTests
{
    private static readonly Guid OfficeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OfficeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task CheckAsync_LocalDuplicate_IsScopedToOffice()
    {
        await using var db = CreateDb();
        SeedOffice(db, OfficeA, "Office A");
        SeedOffice(db, OfficeB, "Office B");

        db.CandidateResponses.Add(CreateEntity(OfficeA, "79001111111", Guid.NewGuid()));
        db.CandidateResponses.Add(CreateEntity(OfficeB, "79001111111", Guid.NewGuid()));
        await db.SaveChangesAsync();

        var current = CreateEntity(OfficeA, "79001111111", Guid.NewGuid());
        db.CandidateResponses.Add(current);
        await db.SaveChangesAsync();

        var sut = new CandidateDuplicateService(db, CreateBitrixClient());
        var result = await sut.CheckAsync(current, null, checkDuplicatesInBitrix: false);

        Assert.True(result.IsLocalDuplicate);
        Assert.False(result.IsBitrixDuplicate);
    }

    [Fact]
    public async Task CheckAsync_PhoneDuplicateOlderThanSixMonths_IsNotLocalDuplicate()
    {
        await using var db = CreateDb();
        SeedOffice(db, OfficeA, "Office A");

        db.CandidateResponses.Add(CreateEntity(
            OfficeA,
            "79003333333",
            Guid.NewGuid(),
            DateTime.UtcNow.AddMonths(-7)));
        await db.SaveChangesAsync();

        var current = CreateEntity(OfficeA, "79003333333", Guid.NewGuid());
        db.CandidateResponses.Add(current);
        await db.SaveChangesAsync();

        var sut = new CandidateDuplicateService(db, CreateBitrixClient());
        var result = await sut.CheckAsync(current, null, checkDuplicatesInBitrix: false);

        Assert.False(result.IsLocalDuplicate);
    }

    [Fact]
    public async Task CheckAsync_DifferentOfficeSamePhone_IsNotLocalDuplicate()
    {
        await using var db = CreateDb();
        SeedOffice(db, OfficeA, "Office A");
        SeedOffice(db, OfficeB, "Office B");

        db.CandidateResponses.Add(CreateEntity(OfficeB, "79002222222", Guid.NewGuid()));
        await db.SaveChangesAsync();

        var current = CreateEntity(OfficeA, "79002222222", Guid.NewGuid());
        db.CandidateResponses.Add(current);
        await db.SaveChangesAsync();

        var sut = new CandidateDuplicateService(db, CreateBitrixClient());
        var result = await sut.CheckAsync(current, null, checkDuplicatesInBitrix: false);

        Assert.False(result.IsLocalDuplicate);
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedOffice(OrbitaDbContext db, Guid id, string name)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = id,
            Name = name,
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true
        });
    }

    private static CandidateResponseEntity CreateEntity(
        Guid officeId,
        string phone,
        Guid id,
        DateTime? createdAt = null) => new()
    {
        Id = id,
        OfficeId = officeId,
        WorkerId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        AccountName = "acc",
        Source = "Avito",
        SourceResponseId = Guid.NewGuid().ToString("N"),
        FullName = "Test User",
        PhoneRaw = phone,
        PhoneNormalized = phone,
        Status = ResponseStatuses.Sent,
        CreatedAt = createdAt ?? DateTime.UtcNow
    };

    private static BitrixClient CreateBitrixClient() =>
        new(new HttpClientFactoryStub(), new CandidateParser());

    private sealed class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
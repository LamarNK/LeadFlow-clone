using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class ResponseSummaryMetricsTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid PersonOne = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid PersonTwo = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public async Task CountUniqueAuthorsAsync_SamePersonDifferentPhones_ReturnsOne()
    {
        await using var db = CreateDb();
        SeedOffice(db);
        db.CandidateResponses.AddRange(
            CreateResponse("r1", PersonOne, "79930099416"),
            CreateResponse("r2", PersonOne, "79910001122"));
        await db.SaveChangesAsync();

        var count = await ResponseSummaryMetrics.CountUniqueAuthorsAsync(
            db.CandidateResponses.AsQueryable(),
            CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CountUniqueAuthorsAsync_TwoPeople_ReturnsTwo()
    {
        await using var db = CreateDb();
        SeedOffice(db);
        db.CandidateResponses.AddRange(
            CreateResponse("r1", PersonOne, "79930099416"),
            CreateResponse("r2", PersonTwo, "79910001122"));
        await db.SaveChangesAsync();

        var count = await ResponseSummaryMetrics.CountUniqueAuthorsAsync(
            db.CandidateResponses.AsQueryable(),
            CancellationToken.None);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountUniqueAuthorsAsync_EmptyPersonId_IsExcluded()
    {
        await using var db = CreateDb();
        SeedOffice(db);
        db.CandidateResponses.AddRange(
            CreateResponse("r1", PersonOne, "79930099416"),
            CreateResponse("r2", Guid.Empty, "79910001122"));
        await db.SaveChangesAsync();

        var count = await ResponseSummaryMetrics.CountUniqueAuthorsAsync(
            db.CandidateResponses.AsQueryable(),
            CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetSummaryAsync_SamePersonDifferentPhones_ReportsOneUniqueAuthor()
    {
        await using var db = CreateDb();
        SeedOffice(db);
        var now = DateTime.UtcNow;
        db.CandidateResponses.AddRange(
            CreateResponse("r1", PersonOne, "79930099416", now.AddHours(-1)),
            CreateResponse("r2", PersonOne, "79910001122", now));
        await db.SaveChangesAsync();

        var sut = new ResponsesQueryService(db, new ResponseBitrixDeliveryService(db));
        var summary = await sut.GetSummaryAsync(
            OfficeScope.ForOffice(OfficeId),
            OfficeId,
            status: null,
            search: null,
            vacancy: null,
            workerId: null,
            accountId: null,
            bitrixDestination: null,
            gender: null,
            ageFrom: null,
            ageTo: null,
            fromUtc: now.AddDays(-1),
            toUtc: now.AddDays(1));

        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.UniqueAuthors);
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedOffice(OrbitaDbContext db)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerId,
            OfficeId = OfficeId,
            DisplayName = "worker",
            MachineName = "pc",
            ApiKeyHash = "hash",
            AppVersion = "1.0",
            MonitoringStatus = "Running",
            LastSeenAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    private static CandidateResponseEntity CreateResponse(
        string sourceResponseId,
        Guid personId,
        string phone,
        DateTime? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        PersonId = personId,
        OfficeId = OfficeId,
        WorkerId = WorkerId,
        AccountId = Guid.NewGuid(),
        AccountName = "acc",
        Source = "Avito",
        SourceResponseId = sourceResponseId,
        FullName = "Гор Олег Александрович",
        PhoneRaw = phone,
        PhoneNormalized = phone,
        Status = ResponseStatuses.Sent,
        CreatedAt = createdAt ?? DateTime.UtcNow
    };
}
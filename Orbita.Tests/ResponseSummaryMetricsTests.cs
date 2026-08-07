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

    [Fact]
    public async Task GetSummaryAsync_CalculatesAverageCollectionTimeFromResponseTime()
    {
        await using var db = CreateDb();
        SeedOffice(db);
        var now = DateTime.UtcNow;
        db.CandidateResponses.AddRange(
            CreateResponse("r1", PersonOne, "79930099416", now.AddMinutes(-20), now),
            CreateResponse("r2", PersonTwo, "79910001122", now.AddMinutes(-70), now),
            CreateResponse("r3", Guid.NewGuid(), "79910002233", now, now));
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

        Assert.Equal(45d, summary.AvgResponseMinutes!.Value);
    }

    [Fact]
    public async Task GetSummaryAsync_SentCountsDeliveriesBySendDate_NotCollectionDate()
    {
        await using var db = CreateDb();
        SeedOffice(db);
        var now = DateTime.UtcNow;
        var bitrixId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        db.BitrixInstances.Add(new BitrixInstanceEntity
        {
            Id = bitrixId,
            OfficeId = OfficeId,
            Name = "Portal A",
            Signature = "pa",
            WebhookUrlProtected = "x",
            ValidationStatus = "Ok",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

        var viaDeliveryId = Guid.NewGuid();
        var legacySentId = Guid.NewGuid();
        var crmCardId = Guid.NewGuid();

        db.CandidateResponses.AddRange(
            new CandidateResponseEntity
            {
                Id = viaDeliveryId,
                OfficeId = OfficeId,
                WorkerId = WorkerId,
                AccountId = Guid.NewGuid(),
                AccountName = "acc",
                Source = "Avito",
                SourceResponseId = "via-delivery",
                FullName = "Delivery Lead",
                PhoneRaw = "+79007777777",
                PhoneNormalized = "79007777777",
                Status = ResponseStatuses.Sent,
                CreatedAt = now.AddDays(-20),
                CollectedAt = now.AddDays(-20),
                ProcessedAt = now.AddDays(-20),
                BitrixInstanceId = bitrixId
            },
            new CandidateResponseEntity
            {
                Id = legacySentId,
                OfficeId = OfficeId,
                WorkerId = WorkerId,
                AccountId = Guid.NewGuid(),
                AccountName = "acc",
                Source = "Avito",
                SourceResponseId = "legacy-sent",
                FullName = "Legacy Lead",
                PhoneRaw = "+79008888888",
                PhoneNormalized = "79008888888",
                Status = ResponseStatuses.Sent,
                CreatedAt = now.AddDays(-20),
                CollectedAt = now.AddDays(-20),
                ProcessedAt = now.AddDays(-2)
            },
            new CandidateResponseEntity
            {
                Id = crmCardId,
                OfficeId = OfficeId,
                WorkerId = WorkerId,
                AccountId = Guid.NewGuid(),
                AccountName = "acc",
                Source = "Avito",
                SourceResponseId = "crm-card",
                FullName = "Crm Lead",
                PhoneRaw = "+79009999999",
                PhoneNormalized = "79009999999",
                Status = ResponseStatuses.Sent,
                CreatedAt = now.AddDays(-20),
                CollectedAt = now.AddDays(-20),
                ProcessedAt = now.AddDays(-20)
            },
            CreateResponse("collected-unsent", Guid.NewGuid(), "79910004455", now.AddHours(-1)));

        db.ResponseBitrixDeliveries.Add(new ResponseBitrixDeliveryEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = viaDeliveryId,
            BitrixInstanceId = bitrixId,
            Outcome = ResponseBitrixDeliveryOutcomes.Sent,
            CreatedAtUtc = now.AddHours(-3),
            Source = "auto"
        });
        db.CrmCandidateCards.Add(new CrmCandidateCardEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = crmCardId,
            OfficeId = OfficeId,
            Stage = "Лид",
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now.AddDays(-1),
            StageChangedAtUtc = now.AddDays(-1),
            IsInActiveLoad = true
        });
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
            fromUtc: now.AddDays(-6),
            toUtc: now);

        // Only the collected-unsent response was collected in the period.
        Assert.Equal(1, summary.Total);
        // Bitrix delivery + legacy ProcessedAt send + legacy CRM card = 3 by actual send date.
        Assert.Equal(3, summary.Sent);
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
        DateTime? createdAt = null,
        DateTime? collectedAt = null) => new()
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
        CreatedAt = createdAt ?? DateTime.UtcNow,
        CollectedAt = collectedAt ?? createdAt ?? DateTime.UtcNow
    };
}

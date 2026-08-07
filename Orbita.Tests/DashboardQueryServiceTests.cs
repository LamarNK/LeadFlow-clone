using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class DashboardQueryServiceTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid BitrixInstanceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public async Task GetGlobalSummaryAsync_WeeklyByDayActivity_UsesDatabaseNotSnapshots()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        // No browser tz → API uses server OS local calendar (null offset).
        var yesterdayLocal = DateTime.Today.AddDays(-1);
        var utcStart = Orbita.Api.Helpers.LocalCalendarDateRange
            .GetUtcRangeForLocalCalendarDay(yesterdayLocal)
            .UtcStartInclusive;

        SeedWorker(db, now);
        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = WorkerId,
            CapturedAtUtc = now,
            StatsJson = """
                {
                  "connectedAccounts":1,
                  "totalToday":0,
                  "sentToCrm":0,
                  "duplicates":0,
                  "hourlyActivity":[],
                  "weeklyByDayActivity":[]
                }
                """,
            BalancesJson = "[]"
        });
        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            WorkerId = WorkerId,
            AccountId = AccountId,
            AccountName = "acc-1",
            Source = "Avito",
            SourceResponseId = "yesterday-response",
            FullName = "User",
            PhoneRaw = "+79001111111",
            PhoneNormalized = "79001111111",
            Status = ResponseStatuses.Sent,
            CreatedAt = utcStart.AddHours(3),
            CollectedAt = utcStart.AddHours(3),
            // Legacy send path: sent time comes from ProcessedAt when no delivery journal exists.
            ProcessedAt = utcStart.AddHours(3)
        });
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var summary = await sut.GetGlobalSummaryAsync(OfficeScope.ForOffice(OfficeId), OfficeId);

        var yesterdayPoint = summary.WeeklyByDayActivity
            .Single(point => point.LocalDate == yesterdayLocal);
        Assert.Equal(1, yesterdayPoint.NewCount);
        Assert.Equal(1, yesterdayPoint.SentCount);
        Assert.Equal(0, yesterdayPoint.DuplicateCount);
    }

    [Fact]
    public async Task GetGlobalSummaryAsync_SentCountedBySendDate_NotCollectionDate()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        var yesterdayLocal = DateTime.Today.AddDays(-1);
        var utcStart = Orbita.Api.Helpers.LocalCalendarDateRange
            .GetUtcRangeForLocalCalendarDay(yesterdayLocal)
            .UtcStartInclusive;

        SeedWorker(db, now);
        var todayLocalStartUtc = Orbita.Api.Helpers.LocalCalendarDateRange
            .GetUtcRangeForLocalCalendarDay(DateTime.Today)
            .UtcStartInclusive;
        var todayNoon = todayLocalStartUtc.AddHours(12);
        // Collected yesterday, but actually sent today via a delivery journal entry.
        var responseId = Guid.NewGuid();
        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = responseId,
            OfficeId = OfficeId,
            WorkerId = WorkerId,
            AccountId = AccountId,
            AccountName = "acc-1",
            Source = "Avito",
            SourceResponseId = "collected-yesterday-sent-today",
            FullName = "User",
            PhoneRaw = "+79001111111",
            PhoneNormalized = "79001111111",
            Status = ResponseStatuses.Sent,
            CreatedAt = utcStart.AddHours(3),
            CollectedAt = utcStart.AddHours(3),
            ProcessedAt = todayNoon
        });
        db.ResponseBitrixDeliveries.Add(new ResponseBitrixDeliveryEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = responseId,
            BitrixInstanceId = BitrixInstanceId,
            Outcome = ResponseBitrixDeliveryOutcomes.Sent,
            CreatedAtUtc = todayNoon,
            Source = "auto"
        });
        await db.SaveChangesAsync();

        var summary = await CreateService(db).GetGlobalSummaryAsync(OfficeScope.ForOffice(OfficeId), OfficeId);

        // Sent lands on today (send time), not on yesterday (collection time).
        Assert.Equal(1, summary.SentToCrm);
        Assert.Equal(0, summary.WeeklyByDayActivity
            .Single(point => point.LocalDate == yesterdayLocal).SentCount);
    }

    [Fact]
    public async Task GetGlobalSummaryAsync_SentCountsCrmAndBitrixDeliveries()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        var todayStart = Orbita.Api.Helpers.LocalCalendarDateRange
            .GetUtcRangeForLocalCalendarDay(DateTime.Today)
            .UtcStartInclusive;
        SeedWorker(db, now);

        var bitrixResponseId = Guid.NewGuid();
        db.CandidateResponses.Add(CreateResponse(todayStart.AddHours(10), ResponseStatuses.Sent, bitrixResponseId));
        db.ResponseBitrixDeliveries.Add(new ResponseBitrixDeliveryEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = bitrixResponseId,
            BitrixInstanceId = BitrixInstanceId,
            Outcome = ResponseBitrixDeliveryOutcomes.Sent,
            CreatedAtUtc = todayStart.AddHours(10),
            Source = "auto"
        });

        var crmResponseId = Guid.NewGuid();
        db.CandidateResponses.Add(CreateResponse(todayStart.AddHours(11), ResponseStatuses.Sent, crmResponseId));
        db.ResponseCrmDeliveries.Add(new ResponseCrmDeliveryEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = crmResponseId,
            OfficeId = OfficeId,
            Outcome = ResponseCrmDeliveryOutcomes.Sent,
            CreatedAtUtc = todayStart.AddHours(11),
            Source = "auto"
        });
        await db.SaveChangesAsync();

        var summary = await CreateService(db).GetGlobalSummaryAsync(OfficeScope.ForOffice(OfficeId), OfficeId);

        Assert.Equal(2, summary.SentToCrm);
    }

    [Fact]
    public async Task GetGlobalSummaryAsync_ReportsTodayResponsesWithoutDuplicates()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedWorker(db, now);
        db.CandidateResponses.AddRange(
            CreateResponse(now, ResponseStatuses.Sent),
            CreateResponse(now, ResponseStatuses.Duplicate),
            CreateResponse(now, ResponseStatuses.InProgress));
        await db.SaveChangesAsync();

        var summary = await CreateService(db).GetGlobalSummaryAsync(OfficeScope.ForOffice(OfficeId), OfficeId);

        Assert.Equal(3, summary.TotalToday);
        Assert.Equal(1, summary.Duplicates);
        Assert.Equal(2, summary.UniqueResponsesToday);
    }

    private static CandidateResponseEntity CreateResponse(DateTime collectedAt, string status) =>
        CreateResponse(collectedAt, status, Guid.NewGuid());

    private static CandidateResponseEntity CreateResponse(DateTime collectedAt, string status, Guid id) => new()
    {
        Id = id,
        OfficeId = OfficeId,
        WorkerId = WorkerId,
        AccountId = AccountId,
        AccountName = "acc-1",
        Source = "Avito",
        SourceResponseId = Guid.NewGuid().ToString(),
        FullName = "User",
        PhoneRaw = "+79001111111",
        PhoneNormalized = "79001111111",
        Status = status,
        CreatedAt = collectedAt,
        CollectedAt = collectedAt
    };

    private static DashboardQueryService CreateService(OrbitaDbContext db) =>
        new(
            db,
            new WorkerReleaseService(Options.Create(new WorkerReleaseOptions())),
            new OfficeScopeService(db),
            new WorkerConnectionRegistry());

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedWorker(OrbitaDbContext db, DateTime now)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "Test Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = now,
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
            MonitoringStatus = "Running",
            LastSeenAtUtc = now,
            CreatedAtUtc = now
        });
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = AccountId,
            DisplayName = "acc-1",
            Status = "Ok",
            IsEnabledInPanel = true,
            UpdatedAtUtc = now
        });
        db.BitrixInstances.Add(new BitrixInstanceEntity
        {
            Id = BitrixInstanceId,
            OfficeId = OfficeId,
            Name = "Portal",
            Signature = "p",
            WebhookUrlProtected = "x",
            ValidationStatus = "Ok",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
    }
}

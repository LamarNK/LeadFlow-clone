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

    [Fact]
    public async Task GetGlobalSummaryAsync_WeeklyByDayActivity_UsesDatabaseNotSnapshots()
    {
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
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
            CreatedAt = utcStart.AddHours(3)
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
    }
}
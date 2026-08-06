using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class MonitoringCycleJournalTests
{
    private static readonly DateTime Day = new(2026, 8, 6);

    [Fact]
    public void BuildFromJournal_SingleDay_DetailedRowsWithNotStarted()
    {
        var cycleStart = TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(10), TimeZoneInfo.Local);
        var sp1Done = cycleStart.AddMinutes(2);
        var cycleId = Guid.NewGuid();
        var cycles = new List<MonitoringCycleRunSnapshot>
        {
            new(
                cycleId,
                "Avito 1",
                cycleStart,
                cycleStart.AddMinutes(5),
                MonitoringCycleRunStatuses.Aborted,
                [
                    new MonitoringSubProfileRunSnapshot(
                        Guid.NewGuid(),
                        "sp-1",
                        "профиль A",
                        Position: 1,
                        Total: 3,
                        StartedAtUtc: cycleStart,
                        CompletedAtUtc: sp1Done,
                        Outcome: MonitoringSubProfileRunOutcomes.Completed,
                        ErrorType: null,
                        ErrorMessage: null,
                        PublishedCount: 2),
                    new MonitoringSubProfileRunSnapshot(
                        Guid.NewGuid(),
                        "sp-2",
                        "профиль B",
                        Position: 2,
                        Total: 3,
                        StartedAtUtc: sp1Done,
                        CompletedAtUtc: sp1Done.AddMinutes(1),
                        Outcome: MonitoringSubProfileRunOutcomes.Failed,
                        ErrorType: "captcha",
                        ErrorMessage: "капча",
                        PublishedCount: 0)
                ])
        };

        var sent = new List<MonitoringCycleSentResponse>
        {
            new("Avito 1", "профиль A", sp1Done),
            new("Avito 1", "профиль A", sp1Done.AddSeconds(1))
        };

        var report = MonitoringCycleReportBuilder.BuildFromJournal(
            cycles,
            Day,
            Day,
            new HashSet<string>(["Avito 1"], StringComparer.OrdinalIgnoreCase),
            sent);

        Assert.True(report.IsDetailed);
        Assert.Equal(2, report.TotalLeads);
        Assert.Single(report.AccountReports);
        Assert.Equal(1, report.AccountReports[0].CycleCount);
        // Only positions with journal rows — no synthetic "#3" from Total=3.
        Assert.Equal(2, report.AccountReports[0].Rows.Count);
        Assert.Equal("профиль A", report.AccountReports[0].Rows[0].Name);
        Assert.Single(report.AccountReports[0].Rows[0].CompletionTimesUtc);
        Assert.Equal(["2"], report.AccountReports[0].Rows[0].LeadsPerCycle);
        Assert.True(report.AccountReports[0].Rows[1].Errors.Count > 0);
        Assert.DoesNotContain(report.AccountReports[0].Rows, r => r.Name.StartsWith("#", StringComparison.Ordinal));
        Assert.Empty(report.AccountReports[0].NotStartedPositions);
    }

    [Fact]
    public void BuildFromJournal_UsesAccountCatalog_AllEnabledSubs_NotOnlyJournalHits()
    {
        var cycleStart = TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(16).AddMinutes(25), TimeZoneInfo.Local);
        var cycles = new List<MonitoringCycleRunSnapshot>
        {
            new(
                Guid.NewGuid(),
                "Avito 11 (3)",
                cycleStart,
                cycleStart.AddMinutes(8),
                MonitoringCycleRunStatuses.Aborted,
                [
                    new MonitoringSubProfileRunSnapshot(
                        Guid.NewGuid(), "sp-tv", "ТрудВахта4", 1, 10,
                        cycleStart, cycleStart.AddMinutes(7),
                        MonitoringSubProfileRunOutcomes.Completed, null, null, 0),
                    new MonitoringSubProfileRunSnapshot(
                        Guid.NewGuid(), "sp-v3", "Ветер3", 2, 10,
                        cycleStart.AddMinutes(7), null,
                        MonitoringSubProfileRunOutcomes.Started, null, null, 0)
                ])
        };

        var catalog = new Dictionary<string, IReadOnlyList<MonitoringAccountSubProfileCatalogEntry>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Avito 11 (3)"] =
            [
                new(1, "sp-wp10", "ВетерПеремен 10"),
                new(2, "sp-wp5", "ВетерПеремен 5"),
                new(3, "sp-wp6", "Ветер Перемен 6"),
                new(4, "sp-wp7", "ВетерПеремен 7"),
                new(5, "sp-wp8", "ВетерПеремен 8"),
                new(6, "sp-wp9", "ВетерПеремен 9"),
                new(7, "sp-wp", "ВетерПеремен"),
                new(8, "sp-wp2", "ВетерПеремен2"),
                new(9, "sp-v3", "Ветер3"),
                new(10, "sp-tv", "ТрудВахта4")
            ]
        };

        var report = MonitoringCycleReportBuilder.BuildFromJournal(
            cycles, Day, Day, accountCatalog: catalog);

        Assert.Single(report.AccountReports);
        Assert.Equal(10, report.AccountReports[0].Rows.Count);
        var trud = Assert.Single(report.AccountReports[0].Rows, r => r.Name == "ТрудВахта4");
        Assert.NotEmpty(trud.CompletionTimesUtc);
        // Completed earlier — must not get "Не запущен" spam.
        Assert.DoesNotContain(trud.Errors, e => e.Detail.Contains("Не запущен", StringComparison.Ordinal));
        var v3 = Assert.Single(report.AccountReports[0].Rows, r => r.Name == "Ветер3");
        Assert.Empty(v3.CompletionTimesUtc);
        // Started in last cycle but not completed — not "не запущен".
        Assert.DoesNotContain(v3.Errors, e => e.Detail.Contains("Не запущен", StringComparison.Ordinal));
        // Others never started in this interrupted cycle → not started.
        Assert.True(report.AccountsWithNotStarted >= 1);
        Assert.Contains(report.AccountReports[0].NotStartedPositions, x => x.Contains("ВетерПеремен 10"));
    }

    [Fact]
    public void BuildFromJournal_CompletedEarlier_NotFlaggedNotStartedOnLaterAbort()
    {
        var t1 = TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(10), TimeZoneInfo.Local);
        var t2 = TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(14), TimeZoneInfo.Local);
        var cycles = new List<MonitoringCycleRunSnapshot>
        {
            new(
                Guid.NewGuid(),
                "Avito 1",
                t1,
                t1.AddMinutes(30),
                MonitoringCycleRunStatuses.Completed,
                [
                    new MonitoringSubProfileRunSnapshot(
                        Guid.NewGuid(), "a", "профиль A", 1, 2, t1, t1.AddMinutes(10),
                        MonitoringSubProfileRunOutcomes.Completed, null, null, 1),
                    new MonitoringSubProfileRunSnapshot(
                        Guid.NewGuid(), "b", "профиль B", 2, 2, t1.AddMinutes(10), t1.AddMinutes(20),
                        MonitoringSubProfileRunOutcomes.Completed, null, null, 0)
                ]),
            new(
                Guid.NewGuid(),
                "Avito 1",
                t2,
                t2.AddMinutes(5),
                MonitoringCycleRunStatuses.Aborted,
                [
                    new MonitoringSubProfileRunSnapshot(
                        Guid.NewGuid(), "a", "профиль A", 1, 2, t2, t2.AddMinutes(3),
                        MonitoringSubProfileRunOutcomes.Completed, null, null, 0)
                ])
        };

        var report = MonitoringCycleReportBuilder.BuildFromJournal(cycles, Day, Day);

        Assert.Equal(2, report.AccountReports[0].Rows.Count);
        var rowB = Assert.Single(report.AccountReports[0].Rows, r => r.Name == "профиль B");
        Assert.NotEmpty(rowB.CompletionTimesUtc);
        Assert.DoesNotContain(rowB.Errors, e => e.Detail.Contains("Не запущен", StringComparison.Ordinal));
        Assert.Empty(report.AccountReports[0].NotStartedPositions);
    }

    [Fact]
    public void BuildFromJournal_MultiDay_IncludesAccountTablesPerDay()
    {
        var t1 = TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(9), TimeZoneInfo.Local);
        var t2 = TimeZoneInfo.ConvertTimeToUtc(Day.AddDays(1).AddHours(11), TimeZoneInfo.Local);
        var cycles = new List<MonitoringCycleRunSnapshot>
        {
            new(
                Guid.NewGuid(),
                "Avito 1",
                t1,
                t1.AddMinutes(10),
                MonitoringCycleRunStatuses.Completed,
                [
                    new MonitoringSubProfileRunSnapshot(
                        Guid.NewGuid(), "a", "main", 1, 1, t1, t1.AddMinutes(3),
                        MonitoringSubProfileRunOutcomes.Completed, null, null, 1)
                ]),
            new(
                Guid.NewGuid(),
                "Avito 1",
                t2,
                t2.AddMinutes(8),
                MonitoringCycleRunStatuses.Completed,
                [
                    new MonitoringSubProfileRunSnapshot(
                        Guid.NewGuid(), "a", "main", 1, 1, t2, t2.AddMinutes(2),
                        MonitoringSubProfileRunOutcomes.Completed, null, null, 1)
                ])
        };
        var sent = new List<MonitoringCycleSentResponse>
        {
            new("Avito 1", "main", t1.AddMinutes(3)),
            new("Avito 1", "main", t2.AddMinutes(2))
        };

        var report = MonitoringCycleReportBuilder.BuildFromJournal(
            cycles,
            Day,
            Day.AddDays(2),
            allowedAccountNames: null,
            sent);

        Assert.True(report.IsDetailed);
        Assert.Equal(2, report.AccountReports.Count);
        Assert.Equal(2, report.TotalLeads);
        Assert.Single(report.LeadSummaries);
        Assert.All(report.AccountReports, a => Assert.Equal("Avito 1", a.AccountName));
        Assert.True(report.AccountReports[0].DateUtc <= report.AccountReports[1].DateUtc);
    }

    [Fact]
    public async Task IngestBatch_UpsertsCycleAndSubProfiles()
    {
        await using var db = CreateDb();
        var workerId = Guid.NewGuid();
        var officeId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "O",
            RegistrationSecretHash = "h",
            CreatedAtUtc = now,
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            DisplayName = "w",
            MachineName = "m",
            ApiKeyHash = "h",
            AppVersion = "1",
            MonitoringStatus = "Running",
            CreatedAtUtc = now
        });
        await db.SaveChangesAsync();

        var cycleId = Guid.NewGuid();
        var subId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var sut = new MonitoringRunIngestService(db);

        var first = await sut.IngestBatchAsync(workerId, new MonitoringRunBatchRequest(
            workerId,
            [
                new MonitoringCycleRunUploadDto(
                    cycleId,
                    accountId,
                    "Avito 7",
                    now.AddMinutes(-10),
                    null,
                    MonitoringCycleRunStatuses.Running,
                    [
                        new MonitoringSubProfileRunUploadDto(
                            subId,
                            "sp-1",
                            "main",
                            1,
                            2,
                            now.AddMinutes(-10),
                            null,
                            MonitoringSubProfileRunOutcomes.Started,
                            null,
                            null)
                    ])
            ]));

        Assert.Null(first.Error);
        Assert.Equal(1, first.Accepted);

        var second = await sut.IngestBatchAsync(workerId, new MonitoringRunBatchRequest(
            workerId,
            [
                new MonitoringCycleRunUploadDto(
                    cycleId,
                    accountId,
                    "Avito 7",
                    now.AddMinutes(-10),
                    now,
                    MonitoringCycleRunStatuses.Completed,
                    [
                        new MonitoringSubProfileRunUploadDto(
                            subId,
                            "sp-1",
                            "main",
                            1,
                            2,
                            now.AddMinutes(-10),
                            now.AddMinutes(-5),
                            MonitoringSubProfileRunOutcomes.Completed,
                            null,
                            null,
                            FoundCount: 3,
                            PublishedCount: 2)
                    ])
            ]));

        Assert.Null(second.Error);
        Assert.Equal(1, await db.MonitoringCycleRuns.CountAsync());
        var cycle = await db.MonitoringCycleRuns.Include(x => x.SubProfileRuns).SingleAsync();
        Assert.Equal(MonitoringCycleRunStatuses.Completed, cycle.Status);
        Assert.NotNull(cycle.FinishedAtUtc);
        Assert.Single(cycle.SubProfileRuns);
        Assert.Equal(MonitoringSubProfileRunOutcomes.Completed, cycle.SubProfileRuns.First().Outcome);
        Assert.Equal(2, cycle.SubProfileRuns.First().PublishedCount);
    }

    [Fact]
    public async Task GetStatisticsAsync_UsesJournalForWeekWithoutLogs()
    {
        await using var db = CreateDb();
        var officeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var workerId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var accountId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var now = DateTime.UtcNow;

        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Office A",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = now,
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            DisplayName = "worker-a",
            MachineName = "pc-a",
            ApiKeyHash = "hash",
            AppVersion = "1.0",
            MonitoringStatus = "Running",
            LastSeenAtUtc = now,
            CreatedAtUtc = now
        });
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = workerId,
            AccountId = accountId,
            DisplayName = "Account A",
            Status = "Active",
            IsEnabledInPanel = true,
            UpdatedAtUtc = now
        });

        var cycleStart = now.AddHours(-3);
        var cycleId = Guid.NewGuid();
        db.MonitoringCycleRuns.Add(new MonitoringCycleRunEntity
        {
            Id = cycleId,
            WorkerId = workerId,
            AccountId = accountId,
            AccountName = "Account A",
            StartedAtUtc = cycleStart,
            FinishedAtUtc = cycleStart.AddMinutes(15),
            Status = MonitoringCycleRunStatuses.Completed,
            IngestedAtUtc = now,
            UpdatedAtUtc = now
        });
        db.MonitoringSubProfileRuns.Add(new MonitoringSubProfileRunEntity
        {
            Id = Guid.NewGuid(),
            CycleRunId = cycleId,
            SubProfileId = "sp-1",
            SubProfileName = "main",
            Position = 1,
            Total = 1,
            StartedAtUtc = cycleStart,
            CompletedAtUtc = cycleStart.AddMinutes(5),
            Outcome = MonitoringSubProfileRunOutcomes.Completed,
            PublishedCount = 1
        });
        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            WorkerId = workerId,
            AccountId = accountId,
            AccountName = "Account A",
            AvitoSubProfileName = "main",
            Source = "Avito",
            SourceResponseId = "r1",
            FullName = "User",
            PhoneRaw = "+79001111111",
            PhoneNormalized = "79001111111",
            Status = ResponseStatuses.Sent,
            CreatedAt = cycleStart.AddMinutes(4),
            CollectedAt = cycleStart.AddMinutes(4),
            ProcessedAt = cycleStart.AddMinutes(5)
        });
        await db.SaveChangesAsync();

        var result = await new OfficeStatisticsQueryService(db, new OfficeScopeService(db), new WorkerConnectionRegistry())
            .GetStatisticsAsync(
                OfficeScope.ForOffice(officeId),
                officeId,
                DateTime.Today.AddDays(-6),
                DateTime.Today);

        Assert.Equal(1, result.MonitoringCycles.TotalLeads);
        Assert.Single(result.MonitoringCycles.LeadSummaries);
        Assert.Equal("Account A", result.MonitoringCycles.LeadSummaries[0].AccountName);
        Assert.True(result.MonitoringCycles.IsDetailed);
        Assert.NotEmpty(result.MonitoringCycles.AccountReports);
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }
}

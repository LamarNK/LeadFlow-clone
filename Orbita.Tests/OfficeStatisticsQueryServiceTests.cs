using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class OfficeStatisticsQueryServiceTests
{
    private static readonly Guid OfficeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OfficeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid WorkerA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid WorkerB = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid AccountA = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid AccountB = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    [Fact]
    public async Task GetStatisticsAsync_OfficeIsolation_ExcludesOtherOffice()
    {
        await using var db = CreateDb();
        SeedOfficeData(db);

        var sut = CreateService(db);
        var result = await sut.GetStatisticsAsync(
            OfficeScope.ForOffice(OfficeA),
            OfficeA,
            DateTime.Today.AddDays(-6),
            DateTime.Today);

        Assert.Single(result.Balances.Accounts);
        Assert.Equal(AccountA, result.Balances.Accounts[0].AccountId);
        Assert.Equal(1, result.Workers.Total);
        Assert.Equal(2, result.Responses.Total);
        Assert.Equal(1, result.HrInsights.TopCities.Count);
        Assert.Equal("Москва", result.HrInsights.TopCities[0].Name);
    }

    [Fact]
    public async Task GetStatisticsAsync_BalanceRollup_AndLowBalanceFlag()
    {
        await using var db = CreateDb();
        SeedOfficeData(db);

        var sut = CreateService(db);
        var result = await sut.GetStatisticsAsync(
            OfficeScope.GlobalAdmin,
            null,
            DateTime.Today,
            DateTime.Today);

        Assert.Equal(2, result.Balances.Accounts.Count);
        Assert.Equal(9000m, result.Balances.TotalAdvance);
        Assert.Equal(1500m, result.Balances.TotalWallet);
        Assert.Equal(1, result.Balances.LowBalanceAccountCount);
        Assert.True(result.Balances.Accounts.Single(a => a.AccountId == AccountB).IsLowBalance);
    }

    [Fact]
    public async Task GetStatisticsAsync_DuplicateSnapshotBalances_DoesNotThrow()
    {
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        var duplicateAccountId = Guid.Parse("3b386125-8751-6cb2-7c9f-a2cfa238d7a6");

        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeA,
            Name = "Office A",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = now,
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerA,
            OfficeId = OfficeA,
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
            WorkerId = WorkerA,
            AccountId = duplicateAccountId,
            DisplayName = "Duplicate Account",
            Status = "Active",
            IsEnabledInPanel = true,
            TotalBalance = 5000m,
            UpdatedAtUtc = now
        });

        var duplicateBalances = $$"""
            [
              {"accountId":"{{duplicateAccountId}}","accountName":"Dup","totalBalance":1000,"subProfiles":[],"totalWalletBalance":0},
              {"accountId":"{{duplicateAccountId}}","accountName":"Dup","totalBalance":2000,"subProfiles":[],"totalWalletBalance":0}
            ]
            """;
        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = WorkerA,
            CapturedAtUtc = now,
            StatsJson = """{"connectedAccounts":1,"activeAdsCount":0,"blockedAdsCount":0}""",
            BalancesJson = duplicateBalances
        });
        db.SaveChanges();

        var sut = CreateService(db);
        var result = await sut.GetStatisticsAsync(
            OfficeScope.ForOffice(OfficeA),
            OfficeA,
            DateTime.Today,
            DateTime.Today);

        Assert.Single(result.Balances.Accounts);
        Assert.Equal(2000m, result.Balances.Accounts[0].Advance);
    }

    [Fact]
    public async Task GetStatisticsAsync_WorkerFilter_LimitsResponsesAndWorkers()
    {
        await using var db = CreateDb();
        SeedOfficeData(db);

        var sut = CreateService(db);
        var result = await sut.GetStatisticsAsync(
            OfficeScope.GlobalAdmin,
            null,
            DateTime.Today.AddDays(-6),
            DateTime.Today,
            [WorkerA]);

        Assert.Equal(1, result.Workers.Total);
        Assert.Equal(2, result.Responses.Total);
        Assert.Equal(1, result.Responses.Sent);
        Assert.DoesNotContain(result.Balances.Accounts, a => a.AccountId == AccountB);
    }

    [Fact]
    public async Task GetStatisticsAsync_DailyTrend_FillsMissingDays()
    {
        await using var db = CreateDb();
        SeedOfficeData(db);

        var sut = CreateService(db);
        var result = await sut.GetStatisticsAsync(
            OfficeScope.ForOffice(OfficeA),
            OfficeA,
            DateTime.Today.AddDays(-2),
            DateTime.Today);

        Assert.Equal(3, result.DailyTrend.Count);
        Assert.Contains(result.DailyTrend, d => d.Total == 0);
        Assert.Equal(2, result.DailyTrend.Sum(d => d.Total));
    }

    [Fact]
    public async Task GetStatisticsAsync_Week_ReturnsSummaryAndTrendForEveryStatus()
    {
        await using var db = CreateDb();
        SeedOfficeData(db);
        var now = DateTime.UtcNow;
        db.CandidateResponses.AddRange(
            CreateResponse(ResponseStatuses.Error, now.AddDays(-3)),
            CreateResponse(ResponseStatuses.ActionRequired, now.AddDays(-4)),
            CreateResponse(ResponseStatuses.InProgress, now.AddDays(-5)));
        await db.SaveChangesAsync();

        var result = await CreateService(db).GetStatisticsAsync(
            OfficeScope.ForOffice(OfficeA),
            OfficeA,
            DateTime.Today.AddDays(-6),
            DateTime.Today);

        Assert.Equal(5, result.Responses.Total);
        Assert.Equal(1, result.Responses.Sent);
        Assert.Equal(1, result.Responses.Duplicates);
        Assert.Equal(1, result.Responses.Errors);
        Assert.Equal(1, result.Responses.ActionRequired);
        Assert.Equal(1, result.Responses.InProgress);
        Assert.Equal(7, result.DailyTrend.Count);
        Assert.Equal(result.Responses.Total, result.DailyTrend.Sum(x => x.Total));
    }

    [Fact]
    public void CandidateResponses_HasIndexForStatisticsWorkerAndPeriodFilter()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(CandidateResponseEntity));

        Assert.NotNull(entity);
        Assert.Contains(
            entity!.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(CandidateResponseEntity.WorkerId), nameof(CandidateResponseEntity.CollectedAt)]));
    }

    [Fact]
    public void ProcessedDurationAverage_TranslatesForPostgreSql()
    {
        using var db = new OrbitaDbContext(
            new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseNpgsql("Host=localhost;Database=orbita;Username=orbita;Password=orbita")
                .Options);

        var sql = db.CandidateResponses
            .Where(x => x.ProcessedAt != null)
            .GroupBy(_ => 1)
            .Select(group => group.Average(x => (double?)(x.ProcessedAt!.Value - x.CollectedAt).TotalMinutes))
            .ToQueryString();

        Assert.Contains("avg", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("date_part", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DailyTrendAggregation_TranslatesToGroupedPostgreSqlQuery()
    {
        using var db = new OrbitaDbContext(
            new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseNpgsql("Host=localhost;Database=orbita;Username=orbita;Password=orbita")
                .Options);

        var sql = db.CandidateResponses
            .GroupBy(x => new { Date = x.CollectedAt.Date, x.CollectedAt.Hour, x.Status })
            .Select(group => new { group.Key.Date, group.Key.Hour, group.Key.Status, Count = group.Count() })
            .ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("date_part", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static OfficeStatisticsQueryService CreateService(OrbitaDbContext db) =>
        new(db, new OfficeScopeService(db), new WorkerConnectionRegistry());

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedOfficeData(OrbitaDbContext db)
    {
        var now = DateTime.UtcNow;
        db.Offices.AddRange(
            new OfficeEntity
            {
                Id = OfficeA,
                Name = "Office A",
                RegistrationSecretHash = "hash",
                CreatedAtUtc = now,
                IsEnabled = true
            },
            new OfficeEntity
            {
                Id = OfficeB,
                Name = "Office B",
                RegistrationSecretHash = "hash",
                CreatedAtUtc = now,
                IsEnabled = true
            });

        db.Workers.AddRange(
            new WorkerEntity
            {
                Id = WorkerA,
                OfficeId = OfficeA,
                DisplayName = "worker-a",
                MachineName = "pc-a",
                ApiKeyHash = "hash",
                AppVersion = "1.0",
                MonitoringStatus = "Running",
                LastSeenAtUtc = now,
                CreatedAtUtc = now
            },
            new WorkerEntity
            {
                Id = WorkerB,
                OfficeId = OfficeB,
                DisplayName = "worker-b",
                MachineName = "pc-b",
                ApiKeyHash = "hash",
                AppVersion = "1.0",
                MonitoringStatus = "Running",
                LastSeenAtUtc = now,
                CreatedAtUtc = now
            });

        db.WorkerAccounts.AddRange(
            new WorkerAccountEntity
            {
                WorkerId = WorkerA,
                AccountId = AccountA,
                DisplayName = "Account A",
                Status = "Active",
                IsEnabledInPanel = true,
                TotalBalance = 8000m,
                UpdatedAtUtc = now
            },
            new WorkerAccountEntity
            {
                WorkerId = WorkerB,
                AccountId = AccountB,
                DisplayName = "Account B",
                Status = "Active",
                IsEnabledInPanel = true,
                TotalBalance = 1000m,
                UpdatedAtUtc = now
            });

        var balancesA = """[{"accountId":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee","accountName":"Account A","totalBalance":8000,"subProfiles":[{"subProfileName":"Main","balance":8000,"walletBalance":1000}],"totalWalletBalance":1000}]""";
        var balancesB = """[{"accountId":"ffffffff-ffff-ffff-ffff-ffffffffffff","accountName":"Account B","totalBalance":1000,"subProfiles":[{"subProfileName":"Main","balance":1000,"walletBalance":500}],"totalWalletBalance":500}]""";

        db.WorkerSnapshots.AddRange(
            new WorkerSnapshotEntity
            {
                Id = Guid.NewGuid(),
                WorkerId = WorkerA,
                CapturedAtUtc = now,
                StatsJson = """{"connectedAccounts":1,"activeAdsCount":3,"blockedAdsCount":0}""",
                BalancesJson = balancesA
            },
            new WorkerSnapshotEntity
            {
                Id = Guid.NewGuid(),
                WorkerId = WorkerB,
                CapturedAtUtc = now,
                StatsJson = """{"connectedAccounts":1,"activeAdsCount":1,"blockedAdsCount":1}""",
                BalancesJson = balancesB
            });

        db.CandidateResponses.AddRange(
            new CandidateResponseEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = OfficeA,
                WorkerId = WorkerA,
                AccountId = AccountA,
                AccountName = "Account A",
                Source = "Avito",
                SourceResponseId = "r1",
                FullName = "User 1",
                Age = 22,
                PhoneRaw = "+79001111111",
                PhoneNormalized = "79001111111",
                City = "Москва",
                Vacancy = "Курьер",
                MessengerUrl = "https://t.me/test",
                Status = ResponseStatuses.Sent,
                CreatedAt = now.AddHours(-2),
                CollectedAt = now.AddHours(-2),
                ProcessedAt = now.AddHours(-1)
            },
            new CandidateResponseEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = OfficeA,
                WorkerId = WorkerA,
                AccountId = AccountA,
                AccountName = "Account A",
                Source = "Avito",
                SourceResponseId = "r2",
                FullName = "User 2",
                Age = 40,
                PhoneRaw = "+79002222222",
                PhoneNormalized = "79002222222",
                City = "Москва",
                Vacancy = "Водитель",
                Status = ResponseStatuses.Duplicate,
                CreatedAt = now.AddDays(-1),
                CollectedAt = now.AddDays(-1)
            },
            new CandidateResponseEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = OfficeB,
                WorkerId = WorkerB,
                AccountId = AccountB,
                AccountName = "Account B",
                Source = "Avito",
                SourceResponseId = "r3",
                FullName = "User 3",
                PhoneRaw = "+79003333333",
                PhoneNormalized = "79003333333",
                City = "Казань",
                Vacancy = "Сборщик",
                Status = ResponseStatuses.Sent,
                CreatedAt = now.AddHours(-1),
                CollectedAt = now.AddHours(-1)
            });

        db.SaveChanges();
    }

    private static CandidateResponseEntity CreateResponse(string status, DateTime collectedAt) => new()
    {
        Id = Guid.NewGuid(),
        OfficeId = OfficeA,
        WorkerId = WorkerA,
        AccountId = AccountA,
        AccountName = "Account A",
        Source = "Avito",
        SourceResponseId = Guid.NewGuid().ToString("N"),
        FullName = "Test User",
        PhoneRaw = "+79000000000",
        PhoneNormalized = Guid.NewGuid().ToString("N"),
        City = "Москва",
        Vacancy = "Курьер",
        Status = status,
        CreatedAt = collectedAt,
        CollectedAt = collectedAt
    };
}

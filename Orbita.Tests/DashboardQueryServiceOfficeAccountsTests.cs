using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

[Collection("PanelAggregateCache")]
public sealed class DashboardQueryServiceOfficeAccountsTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherOfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaab");
    private static readonly Guid WorkerOneId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid WorkerTwoId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbc");
    private static readonly Guid OtherOfficeWorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbd");
    private static readonly Guid AccountOneId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid AccountTwoId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccd");
    private static readonly Guid OtherAccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccce");

    [Fact]
    public async Task GetOfficeAccountsAsync_ReturnsAccountsForAllWorkers_WithSnapshotBalances()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        SeedWorker(db, WorkerOneId, OfficeId, "worker-1", now);
        SeedWorker(db, WorkerTwoId, OfficeId, "worker-2", now);
        SeedAccount(db, WorkerOneId, AccountOneId, "acc-1", now, totalBalance: 1200m);
        SeedAccount(db, WorkerTwoId, AccountTwoId, "acc-2", now, totalBalance: 3400m);
        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = WorkerOneId,
            CapturedAtUtc = now,
            StatsJson = "{}",
            BalancesJson = JsonSerializer.Serialize(new[]
            {
                new WorkerBalanceDto(AccountOneId, "acc-1", 1500m, [], 200m)
            })
        });
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var items = await sut.GetOfficeAccountsAsync(OfficeScope.ForOffice(OfficeId), OfficeId);

        Assert.Equal(2, items.Count);
        var first = Assert.Single(items, x => x.Account.AccountId == AccountOneId);
        Assert.Equal(WorkerOneId, first.WorkerId);
        Assert.Equal("worker-1", first.WorkerDisplayName);
        Assert.Equal(1500m, first.Balance?.TotalBalance);
        Assert.Equal(200m, first.Balance?.TotalWalletBalance);

        var second = Assert.Single(items, x => x.Account.AccountId == AccountTwoId);
        Assert.Equal(WorkerTwoId, second.WorkerId);
        Assert.Equal(3400m, second.Balance?.TotalBalance);
    }

    [Fact]
    public async Task GetOfficeAccountsAsync_DoesNotLeakOtherOfficeAccounts()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        db.Offices.Add(new OfficeEntity
        {
            Id = OtherOfficeId,
            Name = "Other",
            RegistrationSecretHash = "hash-2",
            CreatedAtUtc = now,
            IsEnabled = true
        });
        SeedWorker(db, WorkerOneId, OfficeId, "worker-1", now);
        SeedWorker(db, OtherOfficeWorkerId, OtherOfficeId, "other-worker", now);
        SeedAccount(db, WorkerOneId, AccountOneId, "acc-1", now);
        SeedAccount(db, OtherOfficeWorkerId, OtherAccountId, "secret-acc", now);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var items = await sut.GetOfficeAccountsAsync(OfficeScope.ForOffice(OfficeId), OfficeId);

        var item = Assert.Single(items);
        Assert.Equal(AccountOneId, item.Account.AccountId);
        Assert.DoesNotContain(items, x => x.Account.AccountId == OtherAccountId);
    }

    [Fact]
    public async Task GetOfficeAccountsAsync_FiltersByWorkerId()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        SeedWorker(db, WorkerOneId, OfficeId, "worker-1", now);
        SeedWorker(db, WorkerTwoId, OfficeId, "worker-2", now);
        SeedAccount(db, WorkerOneId, AccountOneId, "acc-1", now);
        SeedAccount(db, WorkerTwoId, AccountTwoId, "acc-2", now);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var items = await sut.GetOfficeAccountsAsync(
            OfficeScope.ForOffice(OfficeId),
            OfficeId,
            WorkerTwoId);

        var item = Assert.Single(items);
        Assert.Equal(WorkerTwoId, item.WorkerId);
        Assert.Equal(AccountTwoId, item.Account.AccountId);
    }

    [Fact]
    public async Task GetOfficeAccountsAsync_UsesCacheUntilAccountsSignal()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        SeedWorker(db, WorkerOneId, OfficeId, "worker-1", now);
        SeedAccount(db, WorkerOneId, AccountOneId, "acc-1", now);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var first = await sut.GetOfficeAccountsAsync(OfficeScope.ForOffice(OfficeId), OfficeId);
        Assert.Single(first);

        SeedAccount(db, WorkerOneId, AccountTwoId, "acc-2", now);
        await db.SaveChangesAsync();

        var cached = await sut.GetOfficeAccountsAsync(OfficeScope.ForOffice(OfficeId), OfficeId);
        Assert.Single(cached);

        PanelAggregateCache.Invalidate([PanelChangeKind.Accounts]);
        var fresh = await sut.GetOfficeAccountsAsync(OfficeScope.ForOffice(OfficeId), OfficeId);
        Assert.Equal(2, fresh.Count);
    }

    [Fact]
    public async Task GetWorkerAccountsAsync_StillReturnsSingleWorkerAccounts()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        SeedWorker(db, WorkerOneId, OfficeId, "worker-1", now);
        SeedAccount(db, WorkerOneId, AccountOneId, "acc-1", now);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var accounts = await sut.GetWorkerAccountsAsync(WorkerOneId, OfficeScope.ForOffice(OfficeId));

        var account = Assert.Single(accounts);
        Assert.Equal(AccountOneId, account.AccountId);
        Assert.Equal("acc-1", account.DisplayName);
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

    private static void SeedOffice(OrbitaDbContext db, DateTime now)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "Test Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = now,
            IsEnabled = true
        });
    }

    private static void SeedWorker(
        OrbitaDbContext db,
        Guid workerId,
        Guid officeId,
        string displayName,
        DateTime now)
    {
        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            DisplayName = displayName,
            MachineName = displayName,
            ApiKeyHash = "hash",
            AppVersion = "1.0",
            MonitoringStatus = "Running",
            LastSeenAtUtc = now,
            CreatedAtUtc = now
        });
    }

    private static void SeedAccount(
        OrbitaDbContext db,
        Guid workerId,
        Guid accountId,
        string displayName,
        DateTime now,
        decimal totalBalance = 0m)
    {
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = workerId,
            AccountId = accountId,
            DisplayName = displayName,
            Status = "Ok",
            IsEnabledInPanel = true,
            TotalBalance = totalBalance,
            UpdatedAtUtc = now
        });
    }
}

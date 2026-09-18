using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

[Collection("PanelAggregateCache")]
public sealed class DashboardQueryServiceWorkersPageTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task GetWorkersPageAsync_PaginatesAfterActivitySort_AndDoesNotLoadAllRows()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);

        var oldest = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb1");
        var middle = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2");
        var newest = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb3");
        SeedWorker(db, oldest, "alpha", now.AddHours(-3));
        SeedWorker(db, middle, "beta", now.AddHours(-2));
        SeedWorker(db, newest, "gamma", now.AddMinutes(-5));
        SeedWorker(db, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb4"), "import", now, machineName: "leadflow-import");
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId),
            OfficeId,
            page: 1,
            pageSize: 2,
            sort: "activity",
            dir: "desc");

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal("activity", page.Sort);
        Assert.Equal("desc", page.Dir);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(newest, page.Items[0].Id);
        Assert.Equal(middle, page.Items[1].Id);
        Assert.DoesNotContain(page.Items, x => x.MachineName == "leadflow-import");
    }

    [Fact]
    public async Task GetWorkersPageAsync_SecondPageContainsRemainingWorkersInGlobalOrder()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        SeedWorker(db, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb1"), "alpha", now.AddHours(-3));
        SeedWorker(db, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2"), "beta", now.AddHours(-2));
        var oldest = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb3");
        SeedWorker(db, oldest, "gamma", now.AddHours(-4));
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId),
            OfficeId,
            page: 2,
            pageSize: 2,
            sort: "activity",
            dir: "desc");

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Page);
        Assert.Single(page.Items);
        Assert.Equal(oldest, page.Items[0].Id);
    }

    [Fact]
    public async Task GetWorkersPageAsync_SearchesAllWorkersBeforePaginating()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        SeedWorker(db, Guid.NewGuid(), "first", now, machineName: "first-pc");
        var target = Guid.NewGuid();
        SeedWorker(db, target, "target", now.AddHours(-1), machineName: "target-pc");
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId),
            OfficeId,
            page: 1,
            pageSize: 1,
            workerSearch: "target-pc");

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(target, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task GetWorkersPageAsync_ClampsPageWhenLastItemDisappears()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        SeedWorker(db, Guid.NewGuid(), "one", now.AddMinutes(-1));
        SeedWorker(db, Guid.NewGuid(), "two", now.AddMinutes(-2));
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId),
            OfficeId,
            page: 5,
            pageSize: 25,
            sort: "activity",
            dir: "desc");

        Assert.Equal(1, page.Page);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task GetWorkersPageAsync_EmptySet_ReturnsEmptyPage()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        SeedOffice(db, DateTime.UtcNow);
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId),
            OfficeId,
            page: 3,
            pageSize: 25);

        Assert.Equal(0, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.EnabledCount);
        Assert.Equal(0, page.PausedCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_DefaultSortIsActivityDescending()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var older = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb01");
        var newer = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb02");
        SeedWorker(db, older, "aaa-first-by-name", now.AddHours(-5));
        SeedWorker(db, newer, "zzz-last-by-name", now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId),
            OfficeId,
            page: 1,
            pageSize: 25);

        Assert.Equal("activity", page.Sort);
        Assert.Equal("desc", page.Dir);
        Assert.Equal(newer, page.Items[0].Id);
        Assert.Equal(older, page.Items[1].Id);
    }

    [Fact]
    public async Task GetWorkersPageAsync_CountsPausedAcrossAllWorkersNotJustPage()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        SeedWorker(db, Guid.NewGuid(), "a", now.AddMinutes(-1), paused: true);
        SeedWorker(db, Guid.NewGuid(), "b", now.AddMinutes(-2), paused: true);
        SeedWorker(db, Guid.NewGuid(), "c", now.AddMinutes(-3), paused: false);
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId),
            OfficeId,
            page: 1,
            pageSize: 1,
            sort: "name",
            dir: "asc");

        Assert.Single(page.Items);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.PausedCount);
        Assert.Equal(1, page.EnabledCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_FiltersPausedWorkersBeforePagination()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        SeedWorker(db, Guid.NewGuid(), "first", now.AddMinutes(-1));
        SeedWorker(db, Guid.NewGuid(), "second", now.AddMinutes(-2));
        var pausedWorker = Guid.NewGuid();
        SeedWorker(db, pausedWorker, "paused", now.AddMinutes(-3), paused: true);
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId),
            OfficeId,
            page: 1,
            pageSize: 1,
            sort: "activity",
            dir: "desc",
            workerFilter: DashboardWorkerFilter.Paused);

        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal(pausedWorker, page.Items[0].Id);
        Assert.Equal(3, page.TabCounts.All);
        Assert.Equal(1, page.TabCounts.Paused);
    }

    [Fact]
    public async Task GetWorkersPageAsync_EmptyFilterIncludesWorkersWithNoActiveAccounts()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var noAccountsWorker = Guid.NewGuid();
        var noActiveAccountsWorker = Guid.NewGuid();
        var activeAccountsWorker = Guid.NewGuid();
        SeedWorker(db, noAccountsWorker, "no accounts", now.AddMinutes(-1));
        SeedWorker(db, noActiveAccountsWorker, "no active accounts", now.AddMinutes(-2));
        SeedWorker(db, activeAccountsWorker, "active accounts", now.AddMinutes(-3));
        SeedAccount(db, noActiveAccountsWorker, "inactive", 500m, status: "Inactive");
        SeedAccount(db, activeAccountsWorker, "active", 500m);
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId),
            OfficeId,
            page: 1,
            pageSize: 25,
            workerFilter: DashboardWorkerFilter.Empty);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.TabCounts.Empty);
        Assert.Equal(
            [noAccountsWorker, noActiveAccountsWorker],
            page.Items.Select(item => item.Id));
        Assert.DoesNotContain(page.Items, item => item.Id == activeAccountsWorker);
    }

    [Fact]
    public async Task GetWorkersPageAsync_SortsResponsesAcrossPagesBeforePaging()
    {
        DashboardQueryService.ClearCacheForTests();
        var (db, connection) = await CreateSqliteDbAsync();
        await using var connectionScope = connection;
        await using var dbScope = db;
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var low = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb11");
        var high = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb12");
        var middle = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb13");
        SeedWorker(db, low, "alpha", now.AddHours(-3));
        SeedWorker(db, high, "beta", now.AddHours(-2));
        SeedWorker(db, middle, "gamma", now.AddHours(-1));
        SeedResponses(db, low, 1, now);
        SeedResponses(db, high, 4, now);
        SeedResponses(db, middle, 2, now);
        await db.SaveChangesAsync();

        var firstPage = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 2, sort: "responses", dir: "desc");
        var secondPage = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 2, pageSize: 2, sort: "responses", dir: "desc");

        Assert.Equal([high, middle], firstPage.Items.Select(x => x.Id));
        Assert.Single(secondPage.Items);
        Assert.Equal(low, secondPage.Items[0].Id);
    }

    [Fact]
    public async Task GetWorkersPageAsync_SortsErrorsAcrossPagesBeforePaging()
    {
        DashboardQueryService.ClearCacheForTests();
        var (db, connection) = await CreateSqliteDbAsync();
        await using var connectionScope = connection;
        await using var dbScope = db;
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var low = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb21");
        var high = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb22");
        var middle = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb23");
        SeedWorker(db, low, "alpha", now.AddHours(-3));
        SeedWorker(db, high, "beta", now.AddHours(-2));
        SeedWorker(db, middle, "gamma", now.AddHours(-1));
        SeedErrors(db, low, 1, now);
        SeedErrors(db, high, 4, now);
        SeedErrors(db, middle, 2, now);
        await db.SaveChangesAsync();

        var firstPage = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 2, sort: "errors", dir: "desc");
        var secondPage = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 2, pageSize: 2, sort: "errors", dir: "desc");

        Assert.Equal([high, middle], firstPage.Items.Select(x => x.Id));
        Assert.Single(secondPage.Items);
        Assert.Equal(low, secondPage.Items[0].Id);
    }

    [Fact]
    public async Task GetWorkersPageAsync_ForceLowBalanceWorkersFirst_PreservingRequestedSortWithinGroups()
    {
        DashboardQueryService.ClearCacheForTests();
        var (db, connection) = await CreateSqliteDbAsync();
        await using var connectionScope = connection;
        await using var dbScope = db;
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var newestLow = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb31");
        var olderLow = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb32");
        var newestNormal = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb33");
        var plainNormal = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb34");
        SeedWorker(db, newestLow, "z-low-newest", now.AddMinutes(-1));
        SeedWorker(db, olderLow, "a-low-older", now.AddMinutes(-4));
        SeedWorker(db, newestNormal, "m-normal-newest", now.AddMinutes(-2));
        SeedWorker(db, plainNormal, "n-normal", now.AddMinutes(-3));
        // Low-balance accounts push their workers to the top even without responses.
        SeedAccount(db, newestLow, "acc-a", totalBalance: 20m, subProfilesJson: """[{"Id":"a","Name":"A","Balance":20}]""");
        SeedAccount(db, newestLow, "acc-a2", totalBalance: 30m, subProfilesJson: """[{"Id":"a2","Name":"A2","Balance":30}]""");
        SeedAccount(db, olderLow, "acc-b", totalBalance: 50m, subProfilesJson: """[{"Id":"b","Name":"B","Balance":50}]""");
        SeedAccount(db, plainNormal, "acc-c", totalBalance: 5000m, subProfilesJson: """[{"Id":"c","Name":"C","Balance":5000}]""");
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25, sort: "activity", dir: "desc");

        // Low-balance workers come first; within each group activity desc is preserved.
        Assert.Equal(
            [newestLow, olderLow, newestNormal, plainNormal],
            page.Items.Select(x => x.Id));
        Assert.Equal(2, page.Items[0].LowBalanceAccountCount);
        Assert.Equal(1, page.Items[1].LowBalanceAccountCount);
        Assert.Equal(0, page.Items[2].LowBalanceAccountCount);
        Assert.Equal(0, page.Items[3].LowBalanceAccountCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_PutsConfirmedZeroBalanceFirst()
    {
        DashboardQueryService.ClearCacheForTests();
        var (db, connection) = await CreateSqliteDbAsync();
        await using var connectionScope = connection;
        await using var dbScope = db;
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var low = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb35");
        var normal = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb36");
        SeedWorker(db, low, "low", now.AddMinutes(-2));
        SeedWorker(db, normal, "normal", now.AddMinutes(-1));
        SeedAccount(
            db,
            low,
            "confirmed-zero",
            totalBalance: 0m,
            subProfilesJson: """[{"Id":"sp-1","Name":"Основной","Balance":0,"WalletBalance":0}]""");
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        Assert.Equal([low, normal], page.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task GetWorkersPageAsync_DoesNotFlagSnapshotOnlyLowBalanceWithoutPersistedRow()
    {
        DashboardQueryService.ClearCacheForTests();
        var (db, connection) = await CreateSqliteDbAsync();
        await using var connectionScope = connection;
        await using var dbScope = db;
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var low = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb37");
        var normal = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb38");
        SeedWorker(db, low, "low", now.AddMinutes(-2));
        SeedWorker(db, normal, "normal", now.AddMinutes(-1));
        var accountId = SeedAccount(db, low, "telemetry-zero", totalBalance: 0m);
        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = low,
            CapturedAtUtc = now,
            StatsJson = "{}",
            BalancesJson = JsonSerializer.Serialize<IReadOnlyList<WorkerBalanceDto>>(
            [
                new WorkerBalanceDto(
                    accountId,
                    "telemetry-zero",
                    0m,
                    [new SubProfileBalanceDto("Основной", 0m)])
            ])
        });
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        Assert.Equal([normal, low], page.Items.Select(x => x.Id));
        Assert.Equal(0, page.Items[0].LowBalanceAccountCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_PrefersPersistedSubprofileBalanceOverStaleSnapshot()
    {
        DashboardQueryService.ClearCacheForTests();
        var (db, connection) = await CreateSqliteDbAsync();
        await using var connectionScope = connection;
        await using var dbScope = db;
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var worker = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb46");
        SeedWorker(db, worker, "worker", now.AddMinutes(-1));
        var accountId = SeedAccount(
            db,
            worker,
            "refreshed",
            totalBalance: 500m,
            subProfilesJson: """[{"Id":"sp-1","Name":"Основной","Balance":500}]""");
        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = worker,
            CapturedAtUtc = now,
            StatsJson = "{}",
            BalancesJson = JsonSerializer.Serialize<IReadOnlyList<WorkerBalanceDto>>(
            [new WorkerBalanceDto(
                accountId,
                "refreshed",
                40m,
                [new SubProfileBalanceDto("Основной", 40m, SubProfileId: "sp-1")])])
        });
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        Assert.Equal(0, Assert.Single(page.Items).LowBalanceAccountCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_DoesNotFlagSnapshotSubprofilesMissingFromBalancesPage()
    {
        DashboardQueryService.ClearCacheForTests();
        var (db, connection) = await CreateSqliteDbAsync();
        await using var connectionScope = connection;
        await using var dbScope = db;
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var worker = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb40");
        SeedWorker(db, worker, "worker", now.AddMinutes(-1));
        var accountId = SeedAccount(db, worker, "high-total", totalBalance: 4209m);
        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = worker,
            CapturedAtUtc = now,
            StatsJson = "{}",
            BalancesJson = JsonSerializer.Serialize<IReadOnlyList<WorkerBalanceDto>>(
            [
                new WorkerBalanceDto(
                    accountId,
                    "high-total",
                    4209m,
                    [
                        new SubProfileBalanceDto("Первый", 47m),
                        new SubProfileBalanceDto("Второй", 53m),
                        new SubProfileBalanceDto("Третий", 4109m)
                    ])
            ])
        });
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        var item = Assert.Single(page.Items);
        Assert.Equal(0, item.LowBalanceAccountCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_KeepsPersistedLowSubProfilesWhenLatestLiveSnapshotOmitsThem()
    {
        DashboardQueryService.ClearCacheForTests();
        var (db, connection) = await CreateSqliteDbAsync();
        await using var connectionScope = connection;
        await using var dbScope = db;
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var worker = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb43");
        SeedWorker(db, worker, "worker", now.AddMinutes(-1));
        var accountId = SeedAccount(
            db,
            worker,
            "high-total",
            totalBalance: 4209m,
            subProfilesJson: """[{"Id":"one","Name":"Первый","Balance":47},{"Id":"two","Name":"Второй","Balance":53}]""");
        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = worker,
            CapturedAtUtc = now,
            StatsJson = "{}",
            BalancesJson = JsonSerializer.Serialize<IReadOnlyList<WorkerBalanceDto>>(
            [new WorkerBalanceDto(accountId, "high-total", 4209m, [])])
        });
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        Assert.Equal(2, Assert.Single(page.Items).LowBalanceAccountCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_CountsLowBalanceAccountsPerWorker()
    {
        DashboardQueryService.ClearCacheForTests();
        var (db, connection) = await CreateSqliteDbAsync();
        await using var connectionScope = connection;
        await using var dbScope = db;
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var worker = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb41");
        SeedWorker(db, worker, "worker", now.AddMinutes(-1));
        // Threshold is inclusive: 100, 249.99 and 250 are low; 250.01 and 500 are not.
        SeedAccount(db, worker, "low-1", totalBalance: 100m, subProfilesJson: """[{"Id":"one","Name":"One","Balance":100}]""");
        SeedAccount(db, worker, "low-2", totalBalance: 249.99m, subProfilesJson: """[{"Id":"two","Name":"Two","Balance":249.99}]""");
        SeedAccount(db, worker, "border", totalBalance: 250m, subProfilesJson: """[{"Id":"three","Name":"Three","Balance":250}]""");
        SeedAccount(db, worker, "above", totalBalance: 250.01m, subProfilesJson: """[{"Id":"five","Name":"Five","Balance":250.01}]""");
        SeedAccount(db, worker, "ok", totalBalance: 500m, subProfilesJson: """[{"Id":"four","Name":"Four","Balance":500}]""");
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        var item = Assert.Single(page.Items);
        Assert.Equal(3, item.LowBalanceAccountCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_ExcludesUnknownBalanceFromLowBalanceCount()
    {
        DashboardQueryService.ClearCacheForTests();
        var (db, connection) = await CreateSqliteDbAsync();
        await using var connectionScope = connection;
        await using var dbScope = db;
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var worker = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb42");
        SeedWorker(db, worker, "worker", now.AddMinutes(-1));
        SeedAccount(db, worker, "unknown", totalBalance: 0m);
        SeedAccount(
            db,
            worker,
            "known-zero",
            totalBalance: 0m,
            subProfilesJson: """[{"Id":"sp-1","Name":"Основной","Balance":0,"WalletBalance":0}]""");
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        Assert.Equal(1, Assert.Single(page.Items).LowBalanceAccountCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_DoesNotFlagDisabledLowBalanceAccount()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var worker = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb45");
        SeedWorker(db, worker, "worker", now.AddMinutes(-1));
        SeedAccount(
            db,
            worker,
            "disabled-low",
            totalBalance: 40m,
            subProfilesJson: """[{"Id":"disabled","Name":"Отключённый","Balance":40}]""",
            isEnabledInPanel: false);
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        Assert.Equal(0, Assert.Single(page.Items).LowBalanceAccountCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_DoesNotFlagDisabledLowBalanceSubProfile()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var worker = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb48");
        SeedWorker(db, worker, "worker", now.AddMinutes(-1));
        SeedAccount(
            db,
            worker,
            "account",
            totalBalance: 40m,
            subProfilesJson: """[{"Id":"disabled","Name":"Отключённый","Balance":40},{"Id":"enabled","Name":"Включённый","Balance":600}]""",
            disabledSubProfileIdsJson: "[\"disabled\"]");
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        Assert.Equal(0, Assert.Single(page.Items).LowBalanceAccountCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_ExcludesOpenTopUpSessionFromLowBalanceCount()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var worker = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb44");
        SeedWorker(db, worker, "worker", now.AddMinutes(-1));
        var openAccount = SeedAccount(
            db,
            worker,
            "open",
            totalBalance: 40m,
            subProfilesJson: """[{"Id":"open","Name":"Открытый","Balance":40}]""");
        SeedAccount(
            db,
            worker,
            "free",
            totalBalance: 50m,
            subProfilesJson: """[{"Id":"free","Name":"Свободный","Balance":50}]""");
        db.TopUpSessions.Add(new TopUpSessionEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = worker,
            AccountId = openAccount,
            AccountName = "open",
            SubProfileId = "open",
            SubProfileName = "Открытый",
            OfficeId = OfficeId,
            OperatorUserId = "op1",
            OperatorDisplayName = "Operator",
            Status = TopUpSessionStatuses.AwaitingBalance,
            CurrentBalance = 40m,
            TargetBalance = 300m,
            RequestedAmount = 260m,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(2)
        });
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        Assert.Equal(1, Assert.Single(page.Items).LowBalanceAccountCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_ExcludesRecentlyCompletedTopUpFromLowBalanceCount()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var worker = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbb47");
        SeedWorker(db, worker, "worker", now.AddMinutes(-1));
        var accountId = SeedAccount(
            db,
            worker,
            "recently-topped-up",
            totalBalance: 40m,
            subProfilesJson: """[{"Id":"sp-1","Name":"Основной","Balance":40}]""");
        db.TopUpSessions.Add(new TopUpSessionEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = worker,
            AccountId = accountId,
            AccountName = "recently-topped-up",
            SubProfileId = "sp-1",
            SubProfileName = "Основной",
            OfficeId = OfficeId,
            OperatorUserId = "op1",
            OperatorDisplayName = "Operator",
            Status = TopUpSessionStatuses.Completed,
            CurrentBalance = 40m,
            TargetBalance = 340m,
            RequestedAmount = 300m,
            CreatedAtUtc = now.AddMinutes(-40),
            CompletedAtUtc = now.AddMinutes(-30),
            ExpiresAtUtc = now.AddHours(2)
        });
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        Assert.Equal(0, Assert.Single(page.Items).LowBalanceAccountCount);
    }

    [Fact]
    public async Task GetWorkersPageAsync_GroupsSubProfilesByAccountAndOrdersLatestAccountFirst()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var workerId = Guid.NewGuid();
        SeedWorker(db, workerId, "worker", now.AddMinutes(-1));
        var olderAccountId = SeedAccount(
            db,
            workerId,
            "Older account",
            totalBalance: 100m,
            subProfilesJson: """[{"Id":"old","Name":"Old profile","Balance":100}]""");
        var latestAccountId = SeedAccount(
            db,
            workerId,
            "Latest account",
            totalBalance: 250m,
            subProfilesJson: """[{"Id":"latest","Name":"Latest profile","Balance":250}]""");
        var olderAccount = db.WorkerAccounts.Local.Single(x => x.AccountId == olderAccountId);
        olderAccount.IsEnabled = false;
        olderAccount.LastMonitoringAt = now.AddHours(-2);
        olderAccount.UpdatedAtUtc = now.AddHours(-2);
        var latestAccount = db.WorkerAccounts.Local.Single(x => x.AccountId == latestAccountId);
        latestAccount.LastMonitoringAt = now.AddMinutes(-5);
        latestAccount.UpdatedAtUtc = now.AddMinutes(-5);
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        var accounts = Assert.Single(page.Items).Accounts!;
        Assert.Collection(
            accounts,
            account =>
            {
                Assert.Equal(latestAccountId, account.Id);
                Assert.True(Assert.Single(account.SubProfiles).IsEnabled);
            },
            account =>
            {
                Assert.Equal(olderAccountId, account.Id);
                Assert.False(Assert.Single(account.SubProfiles).IsEnabled);
            });
    }

    [Fact]
    public async Task GetWorkersPageAsync_ReturnsResponseAndErrorMetricsPerAccount()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        SeedOffice(db, now);
        var workerId = Guid.NewGuid();
        SeedWorker(db, workerId, "worker", now.AddMinutes(-1));
        var firstAccountId = SeedAccount(db, workerId, "First account", 500m);
        var secondAccountId = SeedAccount(db, workerId, "Second account", 600m);

        foreach (var (accountId, status) in new[]
                 {
                     (firstAccountId, ResponseStatuses.New),
                     (firstAccountId, ResponseStatuses.Duplicate),
                     (secondAccountId, ResponseStatuses.New)
                 })
        {
            var personId = Guid.NewGuid();
            db.CandidatePersons.Add(new CandidatePersonEntity
            {
                Id = personId,
                OfficeId = OfficeId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            db.CandidateResponses.Add(new CandidateResponseEntity
            {
                Id = Guid.NewGuid(),
                PersonId = personId,
                OfficeId = OfficeId,
                WorkerId = workerId,
                AccountId = accountId,
                SourceResponseId = Guid.NewGuid().ToString("N"),
                CreatedAt = now,
                CollectedAt = now,
                Status = status
            });
        }

        db.WorkerEvents.AddRange(
            new WorkerEventEntity
            {
                Id = Guid.NewGuid(),
                WorkerId = workerId,
                AccountId = firstAccountId,
                Level = "Warning",
                Message = "First account warning",
                CreatedAtUtc = now
            },
            new WorkerEventEntity
            {
                Id = Guid.NewGuid(),
                WorkerId = workerId,
                AccountId = secondAccountId,
                Level = "Error",
                Message = "Second account error",
                CreatedAtUtc = now
            },
            new WorkerEventEntity
            {
                Id = Guid.NewGuid(),
                WorkerId = workerId,
                AccountId = secondAccountId,
                Level = "Error",
                Message = "Dismissed error",
                CreatedAtUtc = now,
                IsDismissed = true
            });
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetWorkersPageAsync(
            OfficeScope.ForOffice(OfficeId), OfficeId, page: 1, pageSize: 25);

        var accounts = Assert.Single(page.Items).Accounts!;
        var firstAccount = Assert.Single(accounts, account => account.Id == firstAccountId);
        Assert.Equal(2, firstAccount.ResponsesToday);
        Assert.Equal(1, firstAccount.DuplicatesToday);
        Assert.Equal(1, firstAccount.ErrorsToday);

        var secondAccount = Assert.Single(accounts, account => account.Id == secondAccountId);
        Assert.Equal(1, secondAccount.ResponsesToday);
        Assert.Equal(0, secondAccount.DuplicatesToday);
        Assert.Equal(1, secondAccount.ErrorsToday);
    }

    [Fact]
    public void WorkerListPaging_NormalizesOutOfRangeValues()
    {
        Assert.Equal(25, WorkerListPaging.NormalizePageSize(0));
        Assert.Equal(100, WorkerListPaging.NormalizePageSize(500));
        Assert.Equal(2, WorkerListPaging.NormalizePage(9, 25, 26));
        Assert.Equal(1, WorkerListPaging.NormalizePage(0, 25, 0));

        var (column, descending) = WorkerListPaging.NormalizeSort(null, null);
        Assert.Equal("activity", column);
        Assert.True(descending);

        var nameSort = WorkerListPaging.NormalizeSort("name", null);
        Assert.Equal("name", nameSort.Column);
        Assert.False(nameSort.Descending);
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

    private static async Task<(OrbitaDbContext Db, SqliteConnection Connection)> CreateSqliteDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new OrbitaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return (db, connection);
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
        Guid id,
        string displayName,
        DateTime? lastSeenAtUtc,
        bool paused = false,
        string machineName = "pc")
    {
        db.Workers.Add(new WorkerEntity
        {
            Id = id,
            OfficeId = OfficeId,
            DisplayName = displayName,
            MachineName = machineName,
            ApiKeyHash = "hash",
            AppVersion = "1.0",
            MonitoringStatus = "Running",
            LastSeenAtUtc = lastSeenAtUtc,
            CreatedAtUtc = DateTime.UtcNow,
            IsMonitoringPaused = paused
        });
    }

    private static Guid SeedAccount(
        OrbitaDbContext db,
        Guid workerId,
        string displayName,
        decimal totalBalance,
        string subProfilesJson = "[]",
        string status = "Active",
        bool isEnabledInPanel = true,
        string disabledSubProfileIdsJson = "[]")
    {
        var accountId = Guid.NewGuid();
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = workerId,
            AccountId = accountId,
            DisplayName = displayName,
            Status = status,
            IsEnabled = true,
            IsEnabledInPanel = isEnabledInPanel,
            TotalBalance = totalBalance,
            SubProfilesJson = subProfilesJson,
            SubProfilesDisabledIdsJson = disabledSubProfileIdsJson
        });
        return accountId;
    }

    private static void SeedResponses(OrbitaDbContext db, Guid workerId, int count, DateTime now)
    {
        for (var index = 0; index < count; index++)
        {
            var personId = Guid.NewGuid();
            db.CandidatePersons.Add(new CandidatePersonEntity
            {
                Id = personId,
                OfficeId = OfficeId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            db.CandidateResponses.Add(new CandidateResponseEntity
            {
                Id = Guid.NewGuid(),
                PersonId = personId,
                OfficeId = OfficeId,
                WorkerId = workerId,
                AccountId = Guid.NewGuid(),
                SourceResponseId = Guid.NewGuid().ToString("N"),
                CreatedAt = now,
                CollectedAt = now,
                Status = "New"
            });
        }
    }

    private static void SeedErrors(OrbitaDbContext db, Guid workerId, int count, DateTime now)
    {
        for (var index = 0; index < count; index++)
        {
            db.WorkerEvents.Add(new WorkerEventEntity
            {
                Id = Guid.NewGuid(),
                WorkerId = workerId,
                Level = "Error",
                Message = "Test error",
                CreatedAtUtc = now
            });
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    public async Task GetWorkersPageAsync_SortsResponsesAcrossPagesBeforePaging()
    {
        DashboardQueryService.ClearCacheForTests();
        await using var db = CreateDb();
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
        await using var db = CreateDb();
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
    public void WorkerListPaging_NormalizesOutOfRangeValues()
    {
        Assert.Equal(25, WorkerListPaging.NormalizePageSize(null));
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

    private static void SeedResponses(OrbitaDbContext db, Guid workerId, int count, DateTime now)
    {
        for (var index = 0; index < count; index++)
        {
            db.CandidateResponses.Add(new CandidateResponseEntity
            {
                Id = Guid.NewGuid(),
                PersonId = Guid.NewGuid(),
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

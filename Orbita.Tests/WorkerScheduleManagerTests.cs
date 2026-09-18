using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerScheduleManagerTests
{
    [Fact]
    public async Task AddAsync_RejectsAssignmentToSecondGroup()
    {
        await using var db = CreateDb();
        var (officeId, workers) = await SeedAsync(db, 1);
        var sut = new WorkerScheduleManager(db, new NullRealtime());
        await sut.EnsureOfficeAsync(officeId, null, default);
        var groups = await db.WorkerScheduleGroups.OrderBy(x => x.DayOff).ToListAsync();
        var first = await sut.AddAsync(OfficeScope.ForOffice(officeId), null,
            new AddWorkerScheduleRequest(groups[0].Id, [workers[0]], WorkerScheduleShifts.Shift1), null);
        var second = await sut.AddAsync(OfficeScope.ForOffice(officeId), null,
            new AddWorkerScheduleRequest(groups[1].Id, [workers[0]], WorkerScheduleShifts.Shift2), null);
        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.NotNull(second.Conflict);
        Assert.Single(await db.WorkerScheduleAssignments.ToListAsync());
    }

    [Fact]
    public async Task AddAsync_RejectsWorkerAlreadyAssignedToSameGroup()
    {
        await using var db = CreateDb();
        var (officeId, workers) = await SeedAsync(db, 1);
        var sut = new WorkerScheduleManager(db, new NullRealtime());
        await sut.EnsureOfficeAsync(officeId, null, default);
        var group = await db.WorkerScheduleGroups.FirstAsync();

        Assert.True((await sut.AddAsync(OfficeScope.ForOffice(officeId), null,
            new AddWorkerScheduleRequest(group.Id, [workers[0]], WorkerScheduleShifts.Shift1), null)).Success);
        var duplicate = await sut.AddAsync(OfficeScope.ForOffice(officeId), null,
            new AddWorkerScheduleRequest(group.Id, [workers[0]], WorkerScheduleShifts.Shift2), null);

        Assert.False(duplicate.Success);
        Assert.Equal(group.Id, duplicate.Conflict?.ExistingGroupId);
        Assert.Equal(WorkerScheduleShifts.Shift1, (await db.WorkerScheduleAssignments.SingleAsync()).Shift);
    }

    [Fact]
    public async Task AddAsync_RejectsDuplicateWorkerIdsWithoutDatabaseError()
    {
        await using var db = CreateDb();
        var (officeId, workers) = await SeedAsync(db, 1);
        var sut = new WorkerScheduleManager(db, new NullRealtime());
        await sut.EnsureOfficeAsync(officeId, null, default);
        var group = await db.WorkerScheduleGroups.FirstAsync();

        var result = await sut.AddAsync(OfficeScope.ForOffice(officeId), null,
            new AddWorkerScheduleRequest(group.Id, [workers[0], workers[0]], WorkerScheduleShifts.Shift1), null);

        Assert.False(result.Success);
        Assert.Null(result.Conflict);
        Assert.Empty(await db.WorkerScheduleAssignments.ToListAsync());
    }

    [Fact]
    public async Task MoveAsync_ReplacesAssignmentAtomically()
    {
        await using var db = CreateDb();
        var (officeId, workers) = await SeedAsync(db, 1);
        var sut = new WorkerScheduleManager(db, new NullRealtime());
        await sut.EnsureOfficeAsync(officeId, null, default);
        var groups = await db.WorkerScheduleGroups.OrderBy(x => x.DayOff).ToListAsync();
        await sut.AddAsync(OfficeScope.ForOffice(officeId), null, new(groups[0].Id, [workers[0]], WorkerScheduleShifts.Shift1), null);
        var result = await sut.MoveAsync(OfficeScope.ForOffice(officeId), null, new(workers[0], groups[3].Id, WorkerScheduleShifts.Shift2), null);
        var assignment = Assert.Single(await db.WorkerScheduleAssignments.ToListAsync());
        Assert.True(result.Success);
        Assert.Equal(groups[3].Id, assignment.GroupId);
        Assert.Equal(WorkerScheduleShifts.Shift2, assignment.Shift);
    }

    [Fact]
    public async Task AutoDistributeAsync_SpreadsWorkersAcrossSevenGroupsAndBalancesShifts()
    {
        await using var db = CreateDb();
        var (officeId, _) = await SeedAsync(db, 15);
        var sut = new WorkerScheduleManager(db, new NullRealtime());
        var result = await sut.AutoDistributeAsync(OfficeScope.ForOffice(officeId), null);
        var counts = await db.WorkerScheduleAssignments.GroupBy(x => x.GroupId).Select(x => new { x.Key, Count = x.Count(), S1 = x.Count(a => a.Shift == WorkerScheduleShifts.Shift1), S2 = x.Count(a => a.Shift == WorkerScheduleShifts.Shift2) }).ToListAsync();
        Assert.True(result.Success);
        Assert.Equal(15, result.Result!.AssignedCount);
        Assert.Equal(7, counts.Count);
        Assert.True(counts.Max(x => x.Count) - counts.Min(x => x.Count) <= 1);
        Assert.All(counts, x => Assert.True(Math.Abs(x.S1 - x.S2) <= 1));
    }

    [Fact]
    public async Task AutoDistributeAsync_AppliesBalancedReferenceGroupPhases()
    {
        await using var db = CreateDb();
        var (officeId, _) = await SeedAsync(db, 14);
        var sut = new WorkerScheduleManager(db, new NullRealtime());
        await sut.EnsureOfficeAsync(officeId, null, default);
        var groups = await db.WorkerScheduleGroups.Where(x => x.OfficeId == officeId).ToListAsync();
        foreach (var group in groups)
            group.CurrentWeekShift = WorkerScheduleShifts.DayFirst;
        await db.SaveChangesAsync();

        var result = await sut.AutoDistributeAsync(OfficeScope.ForOffice(officeId), null);

        Assert.True(result.Success);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"));
        var today = localNow.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)localNow.DayOfWeek - 1;
        Assert.All(groups, group =>
            Assert.Equal(WorkerScheduleCalculator.ReferenceOrientation(group.DayOff, today), group.CurrentWeekShift));
    }

    [Fact]
    public async Task AutoDistributeAsync_SynchronizesTransitionMarkerBeforeApplyingSchedule()
    {
        await using var db = CreateDb();
        var (officeId, _) = await SeedAsync(db, 7);
        var sut = new WorkerScheduleManager(db, new NullRealtime());
        var settings = await sut.EnsureOfficeAsync(officeId, null, default);
        settings.TimeZoneId = TimeZoneInfo.Utc.Id;
        var now = DateTime.UtcNow;
        var today = now.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)now.DayOfWeek - 1;
        var group = await db.WorkerScheduleGroups.SingleAsync(x => x.OfficeId == officeId && x.DayOff == today);
        group.LastAppliedOffDate = DateOnly.FromDateTime(now).AddDays(-7);
        group.CurrentWeekShift = WorkerScheduleShifts.DayFirst;
        await db.SaveChangesAsync();

        var result = await sut.AutoDistributeAsync(OfficeScope.ForOffice(officeId), null);

        Assert.True(result.Success);
        Assert.Equal(WorkerScheduleCalculator.ReferenceOrientation(today, today), group.CurrentWeekShift);
        Assert.Equal(DateOnly.FromDateTime(now), group.LastAppliedOffDate);
    }

    [Fact]
    public async Task AutoDistributeAsync_IncludesDisabledOfficeWorkers()
    {
        await using var db = CreateDb();
        var (officeId, workers) = await SeedAsync(db, 2);
        (await db.Workers.SingleAsync(x => x.Id == workers[1])).IsEnabled = false;
        await db.SaveChangesAsync();

        var result = await new WorkerScheduleManager(db, new NullRealtime())
            .AutoDistributeAsync(OfficeScope.ForOffice(officeId), null);

        Assert.True(result.Success);
        Assert.Equal(2, result.Result?.AssignedCount);
        Assert.Equal(2, await db.WorkerScheduleAssignments.CountAsync());
    }

    [Fact]
    public async Task AutoDistributeAsync_RebalancesExistingAssignments()
    {
        await using var db = CreateDb();
        var (officeId, workers) = await SeedAsync(db, 15);
        var sut = new WorkerScheduleManager(db, new NullRealtime());
        await sut.EnsureOfficeAsync(officeId, null, default);
        var firstGroup = await db.WorkerScheduleGroups.FirstAsync(x => x.OfficeId == officeId);
        db.WorkerScheduleAssignments.AddRange(workers.Select(id => new WorkerScheduleAssignmentEntity
        {
            WorkerId = id, GroupId = firstGroup.Id, Shift = WorkerScheduleShifts.Shift1
        }));
        await db.SaveChangesAsync();

        var result = await sut.AutoDistributeAsync(OfficeScope.ForOffice(officeId), null);

        Assert.True(result.Success);
        Assert.Equal(15, await db.WorkerScheduleAssignments.CountAsync());
        var counts = await db.WorkerScheduleAssignments.GroupBy(x => x.GroupId).Select(x => x.Count()).ToListAsync();
        Assert.Equal(7, counts.Count);
        Assert.True(counts.Max() - counts.Min() <= 1);
    }

    [Fact]
    public async Task AutoDistributeAsync_SplitsOneHundredFortyWorkersIntoTwentyPerGroup()
    {
        await using var db = CreateDb();
        var (officeId, _) = await SeedAsync(db, 140);

        var result = await new WorkerScheduleManager(db, new NullRealtime())
            .AutoDistributeAsync(OfficeScope.ForOffice(officeId), null);

        Assert.True(result.Success);
        Assert.Equal(140, result.Result?.AssignedCount);
        Assert.Equal(7, result.Result?.GroupCounts.Count);
        Assert.All(result.Result!.GroupCounts.Values, count => Assert.Equal(20, count));
    }

    [Fact]
    public async Task AddAsync_AppliesTodaysDayOffImmediately()
    {
        await using var db = CreateDb();
        var (officeId, workers) = await SeedAsync(db, 1);
        var sut = new WorkerScheduleManager(db, new NullRealtime());
        await sut.EnsureOfficeAsync(officeId, null, default);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"));
        var dayOff = localNow.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)localNow.DayOfWeek - 1;
        var group = await db.WorkerScheduleGroups.SingleAsync(x => x.OfficeId == officeId && x.DayOff == dayOff);

        var result = await sut.AddAsync(OfficeScope.ForOffice(officeId), null,
            new AddWorkerScheduleRequest(group.Id, [workers[0]], WorkerScheduleShifts.Shift1), null);

        Assert.True(result.Success);
        Assert.True((await db.Workers.SingleAsync(x => x.Id == workers[0])).IsMonitoringPaused);
    }

    [Fact]
    public async Task EnsureOfficeAsync_SeedsLastOccurrenceForEveryDayOff()
    {
        await using var db = CreateDb();
        var (officeId, _) = await SeedAsync(db, 0);

        await new WorkerScheduleManager(db, new NullRealtime()).EnsureOfficeAsync(officeId, null, default);

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"));
        var today = localNow.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)localNow.DayOfWeek - 1;
        var localDate = DateOnly.FromDateTime(localNow);
        var groups = await db.WorkerScheduleGroups.ToListAsync();
        Assert.All(groups, group =>
            Assert.Equal(localDate.AddDays(-((today - group.DayOff + 7) % 7)), group.LastAppliedOffDate));
    }

    [Fact]
    public async Task UpdateSettingsAsync_AcceptsComplementaryDayAndNightWindows()
    {
        await using var db = CreateDb();
        var (officeId, _) = await SeedAsync(db, 0);
        var sut = new WorkerScheduleManager(db, new NullRealtime());

        var result = await sut.UpdateSettingsAsync(
            OfficeScope.ForOffice(officeId),
            null,
            new UpdateWorkerScheduleSettingsRequest("07:00", "19:00", "19:00", "07:00", TimeZoneInfo.Utc.Id),
            null);

        Assert.True(result.Success);
        var settings = await db.WorkerScheduleOffices.SingleAsync();
        Assert.Equal("07:00", settings.DayStartLocalTime);
        Assert.Equal("19:00", settings.NightStartLocalTime);
    }

    [Theory]
    [InlineData("07:00", "07:00", "19:00", "07:00")]
    [InlineData("07:00", "20:00", "19:00", "07:00")]
    [InlineData("07:00", "18:00", "19:00", "07:00")]
    public async Task UpdateSettingsAsync_RejectsEmptyOverlappingOrGappedWindows(
        string dayStart,
        string dayEnd,
        string nightStart,
        string nightEnd)
    {
        await using var db = CreateDb();
        var (officeId, _) = await SeedAsync(db, 0);
        var sut = new WorkerScheduleManager(db, new NullRealtime());

        var result = await sut.UpdateSettingsAsync(
            OfficeScope.ForOffice(officeId),
            null,
            new UpdateWorkerScheduleSettingsRequest(dayStart, dayEnd, nightStart, nightEnd),
            null);

        Assert.False(result.Success);
        Assert.Contains("полные сутки", result.Error);
    }

    [Fact]
    public async Task GetAsync_DoesNotExposeAnotherOffice()
    {
        await using var db = CreateDb();
        var (officeId, _) = await SeedAsync(db, 1);
        var other = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity { Id = other, Name = "Other", RegistrationSecretHash = "x", CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var sut = new WorkerScheduleManager(db, new NullRealtime());
        var result = await sut.GetAsync(OfficeScope.ForOffice(officeId), other, null);
        Assert.NotNull(result);
        Assert.Equal(officeId, result.Settings.OfficeId);
    }

    [Fact]
    public async Task GetAsync_ReturnsNextWorkingDayForAssignedWorker()
    {
        await using var db = CreateDb();
        var (officeId, workers) = await SeedAsync(db, 1);
        var sut = new WorkerScheduleManager(db, new NullRealtime());
        await sut.EnsureOfficeAsync(officeId, null, default);
        var group = await db.WorkerScheduleGroups.FirstAsync(x => x.OfficeId == officeId);
        await sut.AddAsync(OfficeScope.ForOffice(officeId), null,
            new AddWorkerScheduleRequest(group.Id, [workers[0]], WorkerScheduleShifts.Shift1), null);

        var result = await sut.GetAsync(OfficeScope.ForOffice(officeId), null, group.DayOff);
        var worker = Assert.Single(Assert.Single(result!.Groups, x => x.Id == group.Id).Workers);

        Assert.False(string.IsNullOrWhiteSpace(worker.NextWorkingDay));
    }

    [Fact]
    public void ScheduleGroup_UsesDatabaseConcurrencyToken()
    {
        using var db = CreateDb();
        var property = db.Model.FindEntityType(typeof(WorkerScheduleGroupEntity))!
            .FindProperty(nameof(WorkerScheduleGroupEntity.RowVersion));

        Assert.NotNull(property);
        Assert.True(property.IsConcurrencyToken);
        Assert.Equal("xmin", property.GetColumnName());
    }

    [Fact]
    public async Task ApplyAsync_SwapsOnlyOnceAndPushesAfterWorkerStateIsSaved()
    {
        await using var db = CreateDb();
        var (officeId, workers) = await SeedAsync(db, 1);
        var initial = new WorkerScheduleManager(db, new NullRealtime());
        var settings = await initial.EnsureOfficeAsync(officeId, null, default);
        settings.TimeZoneId = TimeZoneInfo.Utc.Id;
        var today = DateTime.UtcNow.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)DateTime.UtcNow.DayOfWeek - 1;
        var group = await db.WorkerScheduleGroups.SingleAsync(x => x.OfficeId == officeId && x.DayOff == today);
        group.CurrentWeekShift = WorkerScheduleShifts.DayFirst;
        group.LastAppliedOffDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-7);
        db.WorkerScheduleAssignments.Add(new WorkerScheduleAssignmentEntity
        {
            WorkerId = workers[0],
            GroupId = group.Id,
            Shift = WorkerScheduleShifts.Shift1
        });
        await db.SaveChangesAsync();

        var push = new ObservingPush(() =>
        {
            var entry = db.ChangeTracker.Entries<WorkerEntity>().Single(x => x.Entity.Id == workers[0]);
            Assert.Equal(EntityState.Unchanged, entry.State);
            Assert.True(entry.Entity.IsMonitoringPaused);
        });
        var sut = new WorkerScheduleManager(db, new NullRealtime(), push);

        Assert.Equal(1, await sut.ApplyAsync());
        Assert.Equal(WorkerScheduleShifts.NightFirst, group.CurrentWeekShift);
        Assert.Equal(0, await sut.ApplyAsync());
        Assert.Equal(WorkerScheduleShifts.NightFirst, group.CurrentWeekShift);
        Assert.Equal(1, push.ConfigPushCount);
    }

    [Fact]
    public async Task ApplyAsync_ReconcilesDayOffMissedWhileServiceWasStopped()
    {
        await using var db = CreateDb();
        var (officeId, _) = await SeedAsync(db, 0);
        var sut = new WorkerScheduleManager(db, new NullRealtime());
        var settings = await sut.EnsureOfficeAsync(officeId, null, default);
        settings.TimeZoneId = TimeZoneInfo.Utc.Id;
        var now = DateTime.UtcNow;
        var today = now.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)now.DayOfWeek - 1;
        var yesterday = (today + 6) % 7;
        var latestOffDate = DateOnly.FromDateTime(now).AddDays(-1);
        var group = await db.WorkerScheduleGroups.SingleAsync(x => x.OfficeId == officeId && x.DayOff == yesterday);
        group.CurrentWeekShift = WorkerScheduleShifts.DayFirst;
        group.LastAppliedOffDate = latestOffDate.AddDays(-7);
        await db.SaveChangesAsync();

        await sut.ApplyAsync();

        Assert.Equal(WorkerScheduleShifts.NightFirst, group.CurrentWeekShift);
        Assert.Equal(latestOffDate, group.LastAppliedOffDate);
    }

    private static OrbitaDbContext CreateDb() => new(new DbContextOptionsBuilder<OrbitaDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid OfficeId, Guid[] Workers)> SeedAsync(OrbitaDbContext db, int count)
    {
        var officeId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity { Id = officeId, Name = "Office", RegistrationSecretHash = "x", CreatedAtUtc = DateTime.UtcNow });
        var ids = Enumerable.Range(1, count).Select(_ => Guid.NewGuid()).ToArray();
        db.Workers.AddRange(ids.Select((id, index) => new WorkerEntity { Id = id, OfficeId = officeId, DisplayName = $"Worker {index:00}", MachineName = $"M{index}", ApiKeyHash = "x", CreatedAtUtc = DateTime.UtcNow, IsEnabled = true }));
        await db.SaveChangesAsync();
        return (officeId, ids);
    }

    private sealed class NullRealtime : IPanelRealtimeNotifier
    {
        public void Notify(IReadOnlyList<PanelChangeKind> kinds, Guid? officeId = null, Guid? workerId = null, string? operatorMessage = null, string? operatorMessageVariant = null) { }
    }

    private sealed class ObservingPush(Action onConfigPush) : IWorkerPushNotifier
    {
        public int ConfigPushCount { get; private set; }
        public Task PushConfigChangedAsync(Guid workerId, CancellationToken ct = default)
        {
            onConfigPush();
            ConfigPushCount++;
            return Task.CompletedTask;
        }

        public Task<bool> TryPushCommandAsync(Guid workerId, string command, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> TryPushCaptchaSessionAsync(Guid workerId, WorkerPendingCaptchaSessionDto session, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> TryPushBrowserMonitorSessionAsync(Guid workerId, WorkerPendingBrowserMonitorSessionDto session, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> TryPushLocalChromeLoginSessionAsync(Guid workerId, WorkerPendingLocalChromeLoginDto session, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> TryPushTopUpSessionAsync(Guid workerId, WorkerPendingTopUpSessionDto session, CancellationToken ct = default) => Task.FromResult(false);
        public Task DeliverPendingOnConnectAsync(Guid workerId, CancellationToken ct = default) => Task.CompletedTask;
    }
}

using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class WorkerSnapshotRetentionTests
{
    [Fact]
    public async Task PruneAsync_RemovesExpiredRowsButAlwaysKeepsLatestPerWorker()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new OrbitaDbContext(options);
        var now = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
        var staleWorker = Guid.NewGuid();
        var activeWorker = Guid.NewGuid();
        AddWorkers(db, now, staleWorker, activeWorker);
        var staleOldest = Snapshot(staleWorker, now.AddDays(-5));
        var staleLatest = Snapshot(staleWorker, now.AddDays(-4));
        var expired = Snapshot(activeWorker, now.AddDays(-3));
        var recent = Snapshot(activeWorker, now.AddHours(-1));
        db.WorkerSnapshots.AddRange(staleOldest, staleLatest, expired, recent);
        await db.SaveChangesAsync();

        var deleted = await new WorkerSnapshotRetentionPruner(db).PruneAsync(
            now,
            interBatchDelay: TimeSpan.Zero);

        Assert.Equal(2, deleted);
        var remainingIds = await db.WorkerSnapshots.Select(snapshot => snapshot.Id).ToListAsync();
        Assert.Contains(staleLatest.Id, remainingIds);
        Assert.Contains(recent.Id, remainingIds);
        Assert.DoesNotContain(staleOldest.Id, remainingIds);
        Assert.DoesNotContain(expired.Id, remainingIds);
    }

    [Fact]
    public async Task PruneAsync_StopsAtPerRunLimit()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new OrbitaDbContext(options);
        var now = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
        var workerId = Guid.NewGuid();
        AddWorkers(db, now, workerId);
        db.WorkerSnapshots.AddRange(Enumerable.Range(1, 6)
            .Select(days => Snapshot(workerId, now.AddDays(-days))));
        await db.SaveChangesAsync();

        var deleted = await new WorkerSnapshotRetentionPruner(db).PruneAsync(
            now,
            batchSize: 2,
            maxRows: 3,
            interBatchDelay: TimeSpan.Zero);

        Assert.Equal(3, deleted);
        Assert.Equal(3, await db.WorkerSnapshots.CountAsync());
    }

    private static void AddWorkers(OrbitaDbContext db, DateTime now, params Guid[] workerIds)
    {
        var officeId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Retention test",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = now
        });
        db.Workers.AddRange(workerIds.Select(workerId => new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            DisplayName = workerId.ToString("N"),
            MachineName = "test-machine",
            ApiKeyHash = "hash",
            CreatedAtUtc = now
        }));
    }

    private static WorkerSnapshotEntity Snapshot(Guid workerId, DateTime capturedAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        WorkerId = workerId,
        CapturedAtUtc = capturedAtUtc,
        StatsJson = "{}",
        BalancesJson = "[]"
    };
}

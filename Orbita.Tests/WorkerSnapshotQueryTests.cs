using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class WorkerSnapshotQueryTests
{
    [Fact]
    public void PostgreSqlQuery_UsesLateralIndexLookupWithoutWindowSort()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseNpgsql("Host=localhost;Database=not_opened;Username=not_used;Password=not_used")
            .Options;
        using var db = new OrbitaDbContext(options);

        var sql = WorkerSnapshotQuery.BuildPostgreSqlQuery(db, [Guid.NewGuid(), Guid.NewGuid()])
            .ToQueryString();

        Assert.Contains("LATERAL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ROW_NUMBER", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unnest({", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadLatestAsync_ReturnsLatestSnapshotForEachRequestedWorker()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new OrbitaDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var firstWorkerId = Guid.NewGuid();
        var secondWorkerId = Guid.NewGuid();
        var workerWithoutSnapshotsId = Guid.NewGuid();
        var officeId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Snapshot test",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        foreach (var workerId in new[] { firstWorkerId, secondWorkerId, workerWithoutSnapshotsId })
        {
            db.Workers.Add(new WorkerEntity
            {
                Id = workerId,
                OfficeId = officeId,
                DisplayName = workerId.ToString("N"),
                MachineName = "test-machine",
                ApiKeyHash = "hash",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        var older = new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(), WorkerId = firstWorkerId,
            CapturedAtUtc = new DateTime(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc),
            StatsJson = "{\"value\":1}", BalancesJson = "[]"
        };
        var latest = new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(), WorkerId = firstWorkerId,
            CapturedAtUtc = older.CapturedAtUtc.AddMinutes(1),
            StatsJson = "{\"value\":2}", BalancesJson = "[]"
        };
        var second = new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(), WorkerId = secondWorkerId,
            CapturedAtUtc = older.CapturedAtUtc,
            StatsJson = "{\"value\":3}", BalancesJson = "[]"
        };
        db.WorkerSnapshots.AddRange(older, latest, second);
        await db.SaveChangesAsync();

        var result = await WorkerSnapshotQuery.LoadLatestAsync(
            db,
            [firstWorkerId, secondWorkerId, workerWithoutSnapshotsId]);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.Id == latest.Id);
        Assert.Contains(result, item => item.Id == second.Id);
    }

    [Fact]
    public async Task LoadLatestAsync_EmptyWorkerList_DoesNotQueryDatabase()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseNpgsql("Host=localhost;Database=not_opened;Username=not_used;Password=not_used")
            .Options;
        await using var db = new OrbitaDbContext(options);

        var result = await WorkerSnapshotQuery.LoadLatestAsync(db, []);

        Assert.Empty(result);
    }
}

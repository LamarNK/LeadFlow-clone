using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;

namespace Orbita.Api.Services;

internal sealed class WorkerSnapshotRetentionPruner(OrbitaDbContext db)
{
    internal const int DefaultBatchSize = 2_000;
    internal const int DefaultMaxRowsPerRun = 50_000;
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(48);

    internal async Task<int> PruneAsync(
        DateTime utcNow,
        int batchSize = DefaultBatchSize,
        int maxRows = DefaultMaxRowsPerRun,
        TimeSpan? interBatchDelay = null,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0 || maxRows <= 0)
        {
            return 0;
        }

        var cutoff = utcNow.Subtract(Retention);
        var workerIds = await db.Workers
            .AsNoTracking()
            .Select(worker => worker.Id)
            .ToListAsync(cancellationToken);
        var totalDeleted = 0;
        var delay = interBatchDelay ?? TimeSpan.FromMilliseconds(50);

        foreach (var workerId in workerIds)
        {
            while (totalDeleted < maxRows)
            {
                var limit = Math.Min(batchSize, maxRows - totalDeleted);
                var deleted = db.Database.IsNpgsql()
                    ? await DeletePostgreSqlBatchAsync(workerId, cutoff, limit, cancellationToken)
                    : await DeletePortableBatchAsync(workerId, cutoff, limit, cancellationToken);

                totalDeleted += deleted;
                if (deleted < limit)
                {
                    break;
                }

                if (delay > TimeSpan.Zero && totalDeleted < maxRows)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }

            if (totalDeleted >= maxRows)
            {
                break;
            }
        }

        return totalDeleted;
    }

    private Task<int> DeletePostgreSqlBatchAsync(
        Guid workerId,
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($$"""
            WITH latest AS (
                SELECT "Id"
                FROM "WorkerSnapshots"
                WHERE "WorkerId" = {{workerId}}
                ORDER BY "CapturedAtUtc" DESC
                LIMIT 1
            ), stale AS (
                SELECT ws."Id"
                FROM "WorkerSnapshots" AS ws
                WHERE ws."WorkerId" = {{workerId}}
                  AND ws."CapturedAtUtc" < {{cutoff}}
                  AND ws."Id" <> (SELECT "Id" FROM latest)
                ORDER BY ws."CapturedAtUtc"
                LIMIT {{limit}}
            )
            DELETE FROM "WorkerSnapshots" AS ws
            USING stale
            WHERE ws."Id" = stale."Id"
            """, cancellationToken);

    private async Task<int> DeletePortableBatchAsync(
        Guid workerId,
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken)
    {
        var latestId = await db.WorkerSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.WorkerId == workerId)
            .OrderByDescending(snapshot => snapshot.CapturedAtUtc)
            .Select(snapshot => snapshot.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var stale = await db.WorkerSnapshots
            .Where(snapshot => snapshot.WorkerId == workerId
                               && snapshot.CapturedAtUtc < cutoff
                               && snapshot.Id != latestId)
            .OrderBy(snapshot => snapshot.CapturedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0)
        {
            return 0;
        }

        db.WorkerSnapshots.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }
}

public sealed class WorkerSnapshotRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkerSnapshotRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var pruner = scope.ServiceProvider.GetRequiredService<WorkerSnapshotRetentionPruner>();
                var deleted = await pruner.PruneAsync(DateTime.UtcNow, cancellationToken: stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation(
                        "Deleted {Count} worker snapshots older than {RetentionHours} hours.",
                        deleted,
                        WorkerSnapshotRetentionPruner.Retention.TotalHours);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to prune expired worker snapshots.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}

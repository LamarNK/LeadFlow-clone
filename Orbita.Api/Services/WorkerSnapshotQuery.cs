using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;

namespace Orbita.Api.Services;

internal static class WorkerSnapshotQuery
{
    internal static IQueryable<WorkerSnapshotEntity> BuildPostgreSqlQuery(
        OrbitaDbContext db,
        IReadOnlyCollection<Guid> workerIds)
    {
        var ids = workerIds.Distinct().ToArray();
        return db.WorkerSnapshots
            .FromSqlInterpolated($$"""
                SELECT snapshot.*
                FROM unnest({{ids}}::uuid[]) AS requested(worker_id)
                CROSS JOIN LATERAL (
                    SELECT ws.*
                    FROM "WorkerSnapshots" AS ws
                    WHERE ws."WorkerId" = requested.worker_id
                    ORDER BY ws."CapturedAtUtc" DESC
                    LIMIT 1
                ) AS snapshot
                """)
            .AsNoTracking();
    }

    internal static async Task<List<WorkerSnapshotEntity>> LoadLatestAsync(
        OrbitaDbContext db,
        IReadOnlyCollection<Guid> workerIds,
        CancellationToken cancellationToken = default)
    {
        var ids = workerIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        if (db.Database.IsNpgsql())
        {
            return await BuildPostgreSqlQuery(db, ids).ToListAsync(cancellationToken);
        }

        return await db.WorkerSnapshots
            .AsNoTracking()
            .Where(snapshot => ids.Contains(snapshot.WorkerId))
            .GroupBy(snapshot => snapshot.WorkerId)
            .Select(group => group.OrderByDescending(snapshot => snapshot.CapturedAtUtc).First())
            .ToListAsync(cancellationToken);
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class WorkerSnapshotPostgreSqlPerformanceTests
{
    private const string ConnectionStringVariable = "ORBITA_TEST_POSTGRES_CONNECTION_STRING";

    [PostgreSqlFact]
    public async Task LatestSnapshotQuery_UsesIndexWithoutTempSortAtProductionLikeVolume()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)!;
        var schema = $"snapshot_perf_{Guid.NewGuid():N}";
        var schemaConnectionString = $"{baseConnectionString.Trim().TrimEnd(';')};Search Path={schema}";
        var bootstrapOptions = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseNpgsql(baseConnectionString)
            .Options;

        await using (var bootstrap = new OrbitaDbContext(bootstrapOptions))
        {
            await bootstrap.Database.ExecuteSqlRawAsync($"CREATE SCHEMA \"{schema}\"");
        }

        try
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseNpgsql(schemaConnectionString)
                .Options;
            await using var db = new OrbitaDbContext(options);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "WorkerSnapshots" (
                    "Id" uuid PRIMARY KEY,
                    "WorkerId" uuid NOT NULL,
                    "CapturedAtUtc" timestamp with time zone NOT NULL,
                    "StatsJson" text NOT NULL,
                    "BalancesJson" text NOT NULL
                );
                CREATE INDEX "IX_WorkerSnapshots_WorkerId_CapturedAtUtc"
                    ON "WorkerSnapshots" ("WorkerId", "CapturedAtUtc");
                """);

            var workerIds = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "WorkerSnapshots" ("Id", "WorkerId", "CapturedAtUtc", "StatsJson", "BalancesJson")
                SELECT md5(worker_id::text || ':' || sample::text)::uuid,
                       worker_id,
                       TIMESTAMPTZ '2026-09-08 12:00:00+00' - sample * INTERVAL '1 second',
                       '{}',
                       '[]'
                FROM unnest({{{workerIds}}}::uuid[]) AS workers(worker_id)
                CROSS JOIN generate_series(1, 5000) AS samples(sample)
                """);
            await db.Database.ExecuteSqlRawAsync("ANALYZE \"WorkerSnapshots\"");

            Assert.Equal(100, (await WorkerSnapshotQuery.LoadLatestAsync(db, workerIds)).Count);

            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
                SELECT snapshot.*
                FROM unnest(@workerIds::uuid[]) AS requested(worker_id)
                CROSS JOIN LATERAL (
                    SELECT ws.*
                    FROM "WorkerSnapshots" AS ws
                    WHERE ws."WorkerId" = requested.worker_id
                    ORDER BY ws."CapturedAtUtc" DESC
                    LIMIT 1
                ) AS snapshot
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "workerIds";
            parameter.Value = workerIds;
            command.Parameters.Add(parameter);
            var planJson = (await command.ExecuteScalarAsync())?.ToString();

            Assert.False(string.IsNullOrWhiteSpace(planJson));
            Assert.DoesNotContain("Seq Scan", planJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sort Method", planJson, StringComparison.OrdinalIgnoreCase);
            using var plan = JsonDocument.Parse(planJson!);
            var executionTime = plan.RootElement[0].GetProperty("Execution Time").GetDouble();
            Assert.True(executionTime < 20, $"Expected under 20 ms, actual {executionTime:F3} ms. Plan: {planJson}");
        }
        finally
        {
            await using var bootstrap = new OrbitaDbContext(bootstrapOptions);
            await bootstrap.Database.ExecuteSqlRawAsync($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    private sealed class PostgreSqlFactAttribute : FactAttribute
    {
        public PostgreSqlFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)))
            {
                Skip = $"Set {ConnectionStringVariable} to run PostgreSQL performance tests.";
            }
        }
    }
}

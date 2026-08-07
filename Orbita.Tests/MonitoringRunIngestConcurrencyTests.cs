using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class MonitoringRunIngestConcurrencyTests
{
    private const string ConnectionStringVariable = "ORBITA_TEST_POSTGRES_CONNECTION_STRING";

    [PostgreSqlFact]
    public async Task IngestBatch_ConcurrentInsertOfSameCycle_UpsertConverges()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)!;
        var schema = $"monitoring_race_{Guid.NewGuid():N}";
        var cs = $"{baseConnectionString.Trim().TrimEnd(';')};Search Path={schema}";

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
                .UseNpgsql(cs)
                .Options;

            await using (var db = new OrbitaDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync();
            }

            var workerId = Guid.NewGuid();
            var officeId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var cycleId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            await using (var seed = new OrbitaDbContext(options))
            {
                seed.Offices.Add(new OfficeEntity
                {
                    Id = officeId,
                    Name = "O",
                    RegistrationSecretHash = "h",
                    CreatedAtUtc = now,
                    IsEnabled = true
                });
                seed.Workers.Add(new WorkerEntity
                {
                    Id = workerId,
                    OfficeId = officeId,
                    DisplayName = "w",
                    MachineName = "m",
                    ApiKeyHash = "h",
                    AppVersion = "1",
                    MonitoringStatus = "Running",
                    CreatedAtUtc = now
                });
                await seed.SaveChangesAsync();
            }

            var subId = Guid.NewGuid();
            var request = new MonitoringRunBatchRequest(
                workerId,
                [
                    new MonitoringCycleRunUploadDto(
                        cycleId,
                        accountId,
                        "Avito 1",
                        now,
                        null,
                        MonitoringCycleRunStatuses.Running,
                        [
                            new MonitoringSubProfileRunUploadDto(
                                subId,
                                "sp-1",
                                "main",
                                1,
                                2,
                                now,
                                null,
                                MonitoringSubProfileRunOutcomes.Started,
                                null,
                                null)
                        ])
                ]);

            // Blocker держит незакоммиченную вставку того же цикла: concurrent
            // ingest упрётся в unique conflict и через ON CONFLICT DO UPDATE
            // должен сойтись к одной строке (цикл + субпрофиль).
            await using var blocker = new OrbitaDbContext(options);
            await blocker.Database.BeginTransactionAsync();
            blocker.MonitoringCycleRuns.Add(new MonitoringCycleRunEntity
            {
                Id = cycleId,
                WorkerId = workerId,
                AccountId = accountId,
                AccountName = "Avito 1",
                StartedAtUtc = now,
                Status = MonitoringCycleRunStatuses.Running,
                IngestedAtUtc = now,
                UpdatedAtUtc = now
            });
            await blocker.SaveChangesAsync();

            var ingestTask = Task.Run(async () =>
            {
                await using var serviceDb = new OrbitaDbContext(options);
                return await new MonitoringRunIngestService(serviceDb)
                    .IngestBatchAsync(workerId, request, CancellationToken.None);
            });

            await Task.Delay(500);
            await blocker.Database.CommitTransactionAsync();

            var result = await ingestTask;
            Assert.Null(result.Error);
            Assert.Equal(1, result.Accepted);

            await using var verify = new OrbitaDbContext(options);
            Assert.Equal(1, await verify.MonitoringCycleRuns.CountAsync());
            Assert.Equal(1, await verify.MonitoringSubProfileRuns.CountAsync());
        }
        finally
        {
            await using var cleanup = new OrbitaDbContext(bootstrapOptions);
            await cleanup.Database.ExecuteSqlRawAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }

    [PostgreSqlFact]
    public async Task IngestBatch_ManyParallelIngestsOfSameNewCycle_AllSucceed()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)!;
        var schema = $"monitoring_parallel_{Guid.NewGuid():N}";
        var cs = $"{baseConnectionString.Trim().TrimEnd(';')};Search Path={schema}";

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
                .UseNpgsql(cs)
                .Options;

            await using (var db = new OrbitaDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync();
            }

            var workerId = Guid.NewGuid();
            var officeId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var cycleId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            await using (var seed = new OrbitaDbContext(options))
            {
                seed.Offices.Add(new OfficeEntity
                {
                    Id = officeId,
                    Name = "O",
                    RegistrationSecretHash = "h",
                    CreatedAtUtc = now,
                    IsEnabled = true
                });
                seed.Workers.Add(new WorkerEntity
                {
                    Id = workerId,
                    OfficeId = officeId,
                    DisplayName = "w",
                    MachineName = "m",
                    ApiKeyHash = "h",
                    AppVersion = "1",
                    MonitoringStatus = "Running",
                    CreatedAtUtc = now
                });
                await seed.SaveChangesAsync();
            }

            var subProfiles = Enumerable.Range(1, 20)
                .Select(i => new MonitoringSubProfileRunUploadDto(
                    Guid.NewGuid(),
                    $"sp-{i}",
                    $"профиль {i}",
                    i,
                    20,
                    now,
                    null,
                    MonitoringSubProfileRunOutcomes.Started,
                    null,
                    null))
                .ToList();

            var request = new MonitoringRunBatchRequest(
                workerId,
                [
                    new MonitoringCycleRunUploadDto(
                        cycleId,
                        accountId,
                        "Avito 7",
                        now,
                        null,
                        MonitoringCycleRunStatuses.Running,
                        subProfiles)
                ]);

            async Task<(int Accepted, string? Error)> Run()
            {
                await using var serviceDb = new OrbitaDbContext(options);
                return await new MonitoringRunIngestService(serviceDb)
                    .IngestBatchAsync(workerId, request, CancellationToken.None);
            }

            var results = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => Task.Run(Run)));

            Assert.All(results, r =>
            {
                Assert.Null(r.Error);
                Assert.Equal(1, r.Accepted);
            });

            await using var verify = new OrbitaDbContext(options);
            Assert.Equal(1, await verify.MonitoringCycleRuns.CountAsync());
            Assert.Equal(20, await verify.MonitoringSubProfileRuns.CountAsync());
        }
        finally
        {
            await using var cleanup = new OrbitaDbContext(bootstrapOptions);
            await cleanup.Database.ExecuteSqlRawAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }

    private sealed class PostgreSqlFactAttribute : FactAttribute
    {
        public PostgreSqlFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(ConnectionStringVariable)))
            {
                Skip = $"Set {ConnectionStringVariable} to run PostgreSQL concurrency tests.";
            }
        }
    }
}

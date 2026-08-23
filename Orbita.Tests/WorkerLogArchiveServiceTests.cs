using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerLogArchiveServiceTests
{
    [Fact]
    public async Task IngestBatch_WritesWorkerEntriesToSharedServiceLogFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"orbita-worker-log-test-{Guid.NewGuid():N}");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Logs:SharedRoot"] = root })
                .Build();
            await using var db = CreateDb();
            var workerId = Guid.NewGuid();
            db.Workers.Add(new WorkerEntity
            {
                Id = workerId,
                OfficeId = Guid.NewGuid(),
                DisplayName = "Фермы1-2-3",
                MachineName = "farm-pc",
                ApiKeyHash = "key",
                AppVersion = "1.0.1.32",
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var timestamp = DateTime.UtcNow.AddDays(-8).AddMinutes(-1);
            var archive = new WorkerLogFileArchive(configuration);
            var sut = new WorkerLogArchiveService(db, archive);
            var (accepted, error) = await sut.IngestBatchAsync(workerId,
            [
                new WorkerLogEntryUploadDto(
                    timestamp,
                    "Warning",
                    "[WorkerMonitoringService.RunAsync]",
                    "Profile switched",
                    "trace-worker-1",
                    true),
                new WorkerLogEntryUploadDto(
                    timestamp.AddSeconds(-1),
                    "Warning",
                    "[WorkerMonitoringService.RunAsync]",
                    "No matching message",
                    "trace-worker-2",
                    true)
            ]);

            Assert.Null(error);
            Assert.Equal(2, accepted);

            var expectedFile = Path.Combine(
                root,
                "Orbita.Worker",
                timestamp.ToString("yyyy"),
                timestamp.ToString("MM"),
                $"log-{timestamp:yyyy-MM-dd}.bin");
            Assert.True(File.Exists(expectedFile));

            var page = await new ServiceLogsQueryService(configuration).SearchAsync(
                "Profile switched",
                "Warning",
                "Orbita.Worker",
                timestamp.Date,
                workerId,
                page: 1,
                pageSize: 50);

            var entry = Assert.Single(page.Items);
            Assert.Equal(timestamp, entry.TimestampUtc);
            Assert.Equal("Profile switched", entry.Message);
            Assert.Equal("trace-worker-1", entry.TraceId);
            Assert.Contains($"worker:{workerId:D}", entry.Source, StringComparison.Ordinal);
            Assert.Contains("[WorkerMonitoringService.RunAsync]", entry.Source, StringComparison.Ordinal);
            Assert.Contains("[source-tampered]", entry.Source, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static OrbitaDbContext CreateDb() => new(
        new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}

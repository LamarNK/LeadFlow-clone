using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;
using Orbita.Logging.Audit;

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

    [Fact]
    public void Filter_KeepsAllowlistedStartupFields_AndDropsSecrets()
    {
        var filtered = WorkerLogPropertyAllowlist.Filter(new Dictionary<string, object?>
        {
            ["startup.correlationId"] = "corr-secret-safe",
            ["startup.stage"] = "чтение прокси профиля",
            ["startup.elapsedMs"] = 180000,
            ["localApi.operation"] = "user/list",
            ["localApi.phase"] = "http_response",
            ["localApi.queueWaitMs"] = 12.5,
            ["localApi.durationMs"] = 40,
            ["localApi.outcome"] = "ok",
            ["localApi.httpStatus"] = 200L,
            ["cdp.call"] = "Connect",
            ["error.type"] = "System.TimeoutException",
            ["password"] = "leak-SECRET",
            ["adsPower.apiMessage"] = "proxy http://user:pass@10.1.2.3:8000",
            ["error.message"] = "ws://127.0.0.1:9222/devtools/browser/secret"
        });

        Assert.Equal("corr-secret-safe", filtered["startup.correlationId"]);
        Assert.Equal("чтение прокси профиля", filtered["startup.stage"]);
        Assert.Equal("user/list", filtered["localApi.operation"]);
        Assert.Equal("http_response", filtered["localApi.phase"]);
        Assert.Equal(200L, filtered["localApi.httpStatus"]);
        Assert.Equal("Connect", filtered["cdp.call"]);
        Assert.False(filtered.ContainsKey("password"));
        Assert.False(filtered.ContainsKey("adsPower.apiMessage"));
        Assert.False(filtered.ContainsKey("error.message"));
    }

    [Fact]
    public void Filter_KeepsCandidateTimingFields_AndDropsCandidateContent()
    {
        var filtered = WorkerLogPropertyAllowlist.Filter(new Dictionary<string, object?>
        {
            ["candidates.accountId"] = "54ae6c4f-d7a1-4728-8d21-0800530c1c2e",
            ["candidates.subProfileId"] = "440629974",
            ["candidates.prepare.scrollMs"] = 12345L,
            ["candidates.prepare.scrollDomCalls"] = 42,
            ["candidates.prepare.scrollProfileItemsParsed"] = 900,
            ["candidates.prepare.scrollFullRescans"] = 2,
            ["candidates.prepare.scrollFallbackRescans"] = 1,
            ["candidates.pipeline.messengerMs"] = 6789L,
            ["candidates.fullName"] = "sensitive candidate name",
            ["candidates.phone"] = "79990000000",
            ["candidates.chatMessages"] = "sensitive chat"
        });

        Assert.Equal("54ae6c4f-d7a1-4728-8d21-0800530c1c2e", filtered["candidates.accountId"]);
        Assert.Equal("440629974", filtered["candidates.subProfileId"]);
        Assert.Equal(12345L, filtered["candidates.prepare.scrollMs"]);
        Assert.Equal(42, filtered["candidates.prepare.scrollDomCalls"]);
        Assert.Equal(900, filtered["candidates.prepare.scrollProfileItemsParsed"]);
        Assert.Equal(2, filtered["candidates.prepare.scrollFullRescans"]);
        Assert.Equal(1, filtered["candidates.prepare.scrollFallbackRescans"]);
        Assert.Equal(6789L, filtered["candidates.pipeline.messengerMs"]);
        Assert.DoesNotContain("candidates.fullName", filtered.Keys);
        Assert.DoesNotContain("candidates.phone", filtered.Keys);
        Assert.DoesNotContain("candidates.chatMessages", filtered.Keys);
    }

    [Fact]
    public async Task IngestBatch_WritesAllowlistedStructuredProperties_ToArchivedWorkerLog()
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
                AppVersion = "1.0.1.94",
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var timestamp = DateTime.UtcNow.AddMinutes(-3);
            var archive = new WorkerLogFileArchive(configuration);
            var sut = new WorkerLogArchiveService(db, archive);
            var properties = WorkerLogPropertyAllowlist.ToTransportMap(new Dictionary<string, object?>
            {
                ["startup.correlationId"] = "corr-farm-1",
                ["startup.attempt"] = 1,
                ["startup.stage"] = "чтение прокси профиля",
                ["startup.elapsedMs"] = 1234.0,
                ["localApi.operation"] = "user/list",
                ["localApi.phase"] = "http_response",
                ["localApi.queueWaitMs"] = 50.0,
                ["localApi.durationMs"] = 80.0,
                ["localApi.outcome"] = "ok",
                ["localApi.httpStatus"] = 200,
                ["cdp.call"] = "PagesAsync",
                ["error.type"] = "LeadFlow.Core.Services.AdsPower.AdsPowerLocalApiTimeoutException",
                ["password"] = "leak-SECRET",
                ["adsPower.apiMessage"] = "raw"
            });

            var (accepted, error) = await sut.IngestBatchAsync(workerId,
            [
                new WorkerLogEntryUploadDto(
                    timestamp,
                    "Warning",
                    "[AdsPowerApiClient.GetProfileProxyAsync]",
                    "AdsPower startup: Local API user/list завершён.",
                    "corr-farm-1",
                    false,
                    properties)
            ]);

            Assert.Null(error);
            Assert.Equal(1, accepted);

            var logger = new Logger(Path.Combine(root, "Orbita.Worker"));
            var entries = await logger.ReadEntriesNewerThanAsync(timestamp.AddMinutes(-1), 20);
            var entry = Assert.Single(entries, e => e.Message.Contains("user/list", StringComparison.Ordinal));
            Assert.Contains("corr-farm-1", entry.Properties, StringComparison.Ordinal);
            Assert.Contains("чтение прокси профиля", entry.Properties, StringComparison.Ordinal);
            Assert.Contains("user/list", entry.Properties, StringComparison.Ordinal);
            Assert.Contains("http_response", entry.Properties, StringComparison.Ordinal);
            Assert.Contains("PagesAsync", entry.Properties, StringComparison.Ordinal);
            Assert.DoesNotContain("leak-SECRET", entry.Properties, StringComparison.Ordinal);
            Assert.DoesNotContain("\"adsPower.apiMessage\"", entry.Properties, StringComparison.Ordinal);
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

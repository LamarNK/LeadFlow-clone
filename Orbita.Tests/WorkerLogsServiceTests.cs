using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerLogsServiceTests
{
    private static readonly Guid WorkerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task IngestAndSearch_FiltersByLevelAndQuery()
    {
        await using var db = CreateDb();
        await SeedWorkerAsync(db);
        var service = CreateService(db);

        IReadOnlyList<WorkerLogEntryUploadDto> entries =
        [
            new WorkerLogEntryUploadDto(DateTime.UtcNow.AddMinutes(-10), "Info", "[A.Method]", "Worker started", null, false),
            new WorkerLogEntryUploadDto(DateTime.UtcNow.AddMinutes(-5), "Error", "[B.Method]", "Captcha detected", "trace-1", false)
        ];

        var (accepted, error) = await service.IngestBatchAsync(WorkerId, entries);
        Assert.Null(error);
        Assert.Equal(2, accepted);

        var errors = await service.SearchAsync(WorkerId, null, "Error", null, 1, 50);
        Assert.Single(errors.Items);
        Assert.Equal("Captcha detected", errors.Items[0].Message);

        var searched = await service.SearchAsync(WorkerId, "started", null, null, 1, 50);
        Assert.Single(searched.Items);
        Assert.Equal("[A.Method]", searched.Items[0].Source);
    }

    [Fact]
    public async Task IngestBatch_IsIdempotentByDedupHash()
    {
        await using var db = CreateDb();
        await SeedWorkerAsync(db);
        var service = CreateService(db);

        var entry = new WorkerLogEntryUploadDto(
            DateTime.UtcNow.AddMinutes(-1),
            "Warning",
            "[Sync.Method]",
            "Retry scheduled",
            null,
            false);

        var first = await service.IngestBatchAsync(WorkerId, [entry]);
        var second = await service.IngestBatchAsync(WorkerId, [entry]);

        Assert.Equal(1, first.Accepted);
        Assert.Equal(0, second.Accepted);
        Assert.Equal(1, await db.WorkerLogEntries.CountAsync());
    }

    [Fact]
    public async Task PruneExpired_RemovesOldEntries()
    {
        await using var db = CreateDb();
        await SeedWorkerAsync(db);
        var service = CreateService(db, retentionDays: 30);

        db.WorkerLogEntries.AddRange(
            new WorkerLogEntryEntity
            {
                WorkerId = WorkerId,
                TimestampUtc = DateTime.UtcNow.AddDays(-40),
                Level = "Info",
                Source = "[Old.Method]",
                Message = "old",
                DedupHash = "hash-old",
                IngestedAtUtc = DateTime.UtcNow.AddDays(-40)
            },
            new WorkerLogEntryEntity
            {
                WorkerId = WorkerId,
                TimestampUtc = DateTime.UtcNow.AddDays(-2),
                Level = "Info",
                Source = "[New.Method]",
                Message = "new",
                DedupHash = "hash-new",
                IngestedAtUtc = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var removed = await service.PruneExpiredAsync();
        Assert.Equal(1, removed);
        Assert.Single(await db.WorkerLogEntries.ToListAsync());
        Assert.Equal("new", (await db.WorkerLogEntries.SingleAsync()).Message);
    }

    private static WorkerLogsService CreateService(OrbitaDbContext db, int retentionDays = 30) =>
        new(db, Options.Create(new WorkerLogsOptions
        {
            RetentionDays = retentionDays,
            MaxBatchSize = 500
        }));

    private static async Task SeedWorkerAsync(OrbitaDbContext db)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "Test Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerId,
            OfficeId = OfficeId,
            DisplayName = "Worker-1",
            ApiKeyHash = "key-hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }
}
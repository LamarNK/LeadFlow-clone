using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class AvitoAdsSyncServiceTests
{
    [Fact]
    public async Task Sync_UpsertsAndDoesNotDuplicate()
    {
        await using var db = await CreateDbAsync();
        var notifier = new FakeNotifier();
        var service = new AvitoAdsSyncService(db, notifier);
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity
        {
            Id = Guid.NewGuid(),
            Name = "Office",
            RegistrationSecretHash = "h",
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = db.Offices.Local.First().Id,
            DisplayName = "W",
            MachineName = "m",
            ApiKeyHash = "k"
        });
        await db.SaveChangesAsync();

        var request = new WorkerAvitoAdSyncRequest(
            workerId,
            accountId,
            "sp-1",
            true,
            DateTime.UtcNow,
            [
                new WorkerAvitoAdSyncItemDto(
                    "8302808573",
                    "Логист в офис",
                    "/item/1",
                    "Активно",
                    DateTime.UtcNow.AddDays(-4),
                    AvitoAdPublicationDateSources.Exact,
                    4,
                    27,
                    DateTime.UtcNow.AddDays(27),
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    true,
                    AvitoAdListingStates.Active,
                    null)
            ]);

        var first = await service.SaveSubProfileSyncAsync(workerId, request, CancellationToken.None);
        var second = await service.SaveSubProfileSyncAsync(workerId, request, CancellationToken.None);

        Assert.Equal(1, first.Upserted);
        Assert.Equal(1, second.Upserted);
        Assert.Equal(1, await db.WorkerAvitoAds.CountAsync());
        var stored = await db.WorkerAvitoAds.SingleAsync();
        Assert.Equal("8302808573", stored.AvitoItemId);
        Assert.Equal("sp-1", stored.AvitoSubProfileId);
        Assert.Contains(PanelChangeKind.Listings, notifier.Kinds);
    }

    private static async Task<OrbitaDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new OrbitaDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class FakeNotifier : IPanelRealtimeNotifier
    {
        public List<PanelChangeKind> Kinds { get; } = [];

        public void Notify(
            IReadOnlyList<PanelChangeKind> kinds,
            Guid? officeId = null,
            Guid? workerId = null,
            string? operatorMessage = null,
            string? operatorMessageVariant = null)
        {
            Kinds.AddRange(kinds);
        }

    }
}

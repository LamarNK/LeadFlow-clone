using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class PanelUserPresenceStoreTests
{
    [Fact]
    public async Task TouchAsync_WritesOneHourRowUntilTheUtcHourChanges()
    {
        await using var db = await CreateDbAsync();
        db.PanelUserProfiles.Add(new PanelUserProfileEntity { UserId = "u1" });
        await db.SaveChangesAsync();

        var t0 = new DateTime(2026, 8, 22, 8, 10, 0, DateTimeKind.Utc);
        await PanelUserPresenceStore.TouchAsync(db, await ProfileAsync(db, "u1"), t0);
        await PanelUserPresenceStore.TouchAsync(db, await ProfileAsync(db, "u1"), t0.AddMinutes(40));
        await PanelUserPresenceStore.TouchAsync(db, await ProfileAsync(db, "u1"), t0.AddHours(1));

        var hours = await db.PanelUserPresenceHours.OrderBy(x => x.HourUtc).ToListAsync();
        Assert.Equal(
            [
                new DateTime(2026, 8, 22, 8, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc)
            ],
            hours.Select(x => x.HourUtc));
        Assert.Equal(t0.AddHours(1), (await ProfileAsync(db, "u1")).LastSeenAtUtc);
    }

    [Fact]
    public async Task GetHourSeriesAsync_CountsDistinctUsersInTheMoscowHour()
    {
        await using var db = await CreateDbAsync();
        db.PanelUserPresenceHours.AddRange(
            new PanelUserPresenceHourEntity { UserId = "a", HourUtc = new DateTime(2026, 8, 22, 8, 0, 0, DateTimeKind.Utc) },
            new PanelUserPresenceHourEntity { UserId = "b", HourUtc = new DateTime(2026, 8, 22, 8, 0, 0, DateTimeKind.Utc) },
            new PanelUserPresenceHourEntity { UserId = "a", HourUtc = new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();

        var series = await PanelUserPresenceStore.GetHourSeriesAsync(
            db,
            new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, series.TodayByHour[11]);
        Assert.Equal(1, series.TodayByHour[12]);
        Assert.Equal(11, series.TypicalPeakHour);
    }

    private static async Task<OrbitaDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new OrbitaDbContext(options);
        db.Database.SetDbConnection(connection);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static Task<PanelUserProfileEntity> ProfileAsync(OrbitaDbContext db, string userId) =>
        db.PanelUserProfiles.SingleAsync(x => x.UserId == userId);
}
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

internal static class PanelUserPresenceStore
{
    public static async Task TouchAsync(
        OrbitaDbContext db,
        PanelUserProfileEntity profile,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        var now = nowUtc.Kind == DateTimeKind.Utc
            ? nowUtc
            : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var hour = PanelUserPresenceRules.TruncateToUtcHour(now);
        var previousHour = profile.LastSeenAtUtc is DateTime lastSeen
            ? PanelUserPresenceRules.TruncateToUtcHour(lastSeen)
            : (DateTime?)null;

        profile.LastSeenAtUtc = now;
        await db.SaveChangesAsync(ct);

        if (previousHour == hour)
        {
            return;
        }

        var exists = await db.PanelUserPresenceHours
            .AnyAsync(x => x.UserId == profile.UserId && x.HourUtc == hour, ct);
        if (!exists)
        {
            db.PanelUserPresenceHours.Add(new PanelUserPresenceHourEntity
            {
                UserId = profile.UserId,
                HourUtc = hour
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Entries<PanelUserPresenceHourEntity>()
                    .Where(e => e.State == EntityState.Added)
                    .ToList()
                    .ForEach(e => e.State = EntityState.Detached);
            }
        }

        var cutoff = hour.AddDays(-PanelUserPresenceRules.RetentionDays);
        await db.PanelUserPresenceHours
            .Where(x => x.HourUtc < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    public static async Task<PanelUserPresenceHourSeriesDto> GetHourSeriesAsync(
        OrbitaDbContext db,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        var now = nowUtc.Kind == DateTimeKind.Utc
            ? nowUtc
            : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var fromUtc = PanelUserPresenceRules.TruncateToUtcHour(now.AddDays(-PanelUserPresenceRules.LookbackDays));
        var hours = await db.PanelUserPresenceHours
            .AsNoTracking()
            .Where(x => x.HourUtc >= fromUtc)
            .Select(x => x.HourUtc)
            .ToListAsync(ct);

        return PanelUserPresenceRules.BuildHourSeries(hours, now);
    }

    public static Task DeleteUserAsync(OrbitaDbContext db, string userId, CancellationToken ct = default) =>
        db.PanelUserPresenceHours
            .Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(ct);
}
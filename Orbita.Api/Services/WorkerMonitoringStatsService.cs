using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerMonitoringStatsService(OrbitaDbContext db)
{
    private const int HeatLookbackDays = 56;
    private const int HeatMinSamples = 24;

    public async Task<WorkerMonitoringStatsDto?> GetForWorkerAsync(Guid workerId, CancellationToken ct = default)
    {
        var worker = await db.Workers
            .AsNoTracking()
            .Where(x => x.Id == workerId)
            .Select(x => new { x.OfficeId })
            .FirstOrDefaultAsync(ct);
        if (worker is null)
        {
            return null;
        }

        var utcNow = DateTime.UtcNow;
        var heatCutoff = utcNow.AddDays(-HeatLookbackDays);
        var timestamps = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.OfficeId == worker.OfficeId && x.CollectedAt >= heatCutoff)
            .Select(x => x.CollectedAt)
            .ToListAsync(ct);

        var heat = ComputeHeatScore(timestamps, utcNow, TimeZoneInfo.Local);

        var utcStart = DateTime.Today.ToUniversalTime();
        var utcEnd = utcStart.AddDays(1);
        var today = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.OfficeId == worker.OfficeId && x.CollectedAt >= utcStart && x.CollectedAt < utcEnd)
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Count(string status) => today.FirstOrDefault(x => x.Status == status)?.Count ?? 0;
        var totalToday = today.Sum(x => x.Count);

        var accounts = await db.WorkerAccounts
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId)
            .ToListAsync(ct);

        var stats = new DashboardStatsDto(
            Count("New"),
            totalToday,
            Count("Sent"),
            Count("InProgress"),
            Count("Duplicate"),
            Count("Error"),
            Count("ActionRequired"),
            accounts.Count(x => x.IsEnabledInPanel),
            accounts.Count(x => x.Status == "RequiresLogin"),
            accounts.Count(x => x.IsEnabledInPanel
                                && x.Status is "RequiresLogin" or "RequiresManualAction" or "Error"),
            accounts.Sum(x => x.ActiveAdsCount),
            accounts.Sum(x => x.BlockedCount),
            accounts.Sum(x => x.DraftsCount),
            [],
            []);

        return new WorkerMonitoringStatsDto(heat, stats);
    }

    private static double ComputeHeatScore(IReadOnlyList<DateTime> createdAtUtcSamples, DateTime utcNow, TimeZoneInfo localTz)
    {
        if (createdAtUtcSamples.Count < HeatMinSamples)
        {
            return 0;
        }

        var buckets = new Dictionary<(int Dow, int Hour), int>();
        foreach (var raw in createdAtUtcSamples)
        {
            var utc = raw.Kind switch
            {
                DateTimeKind.Utc => raw,
                DateTimeKind.Local => raw.ToUniversalTime(),
                _ => DateTime.SpecifyKind(raw, DateTimeKind.Utc)
            };
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, localTz);
            var key = ((int)local.DayOfWeek, local.Hour);
            buckets[key] = buckets.GetValueOrDefault(key) + 1;
        }

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(utcNow, localTz);
        var dow = (int)nowLocal.DayOfWeek;
        var hour = nowLocal.Hour;

        double WeightedWindow(int d, int h)
        {
            var (d0, h0) = AddLocalHours(d, h, -1);
            var (d2, h2) = AddLocalHours(d, h, 1);
            return 0.25 * buckets.GetValueOrDefault((d0, h0))
                   + 0.5 * buckets.GetValueOrDefault((d, h))
                   + 0.25 * buckets.GetValueOrDefault((d2, h2));
        }

        var maxWindow = 0.0;
        for (var d = 0; d < 7; d++)
        {
            for (var h = 0; h < 24; h++)
            {
                maxWindow = Math.Max(maxWindow, WeightedWindow(d, h));
            }
        }

        return maxWindow <= 0 ? 0 : Math.Clamp(WeightedWindow(dow, hour) / maxWindow, 0, 1);
    }

    private static (int Dow, int Hour) AddLocalHours(int dow, int hour, int deltaHours)
    {
        var h = hour + deltaHours;
        var d = dow;
        while (h < 0)
        {
            h += 24;
            d = (d + 6) % 7;
        }

        while (h > 23)
        {
            h -= 24;
            d = (d + 1) % 7;
        }

        return (d, h);
    }
}
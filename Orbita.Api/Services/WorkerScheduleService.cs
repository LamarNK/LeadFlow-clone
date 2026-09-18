using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerScheduleService(
    OrbitaDbContext db,
    LeadExportQuotaService leadExportQuota,
    IPanelRealtimeNotifier panelRealtime,
    IWorkerPushNotifier workerPushNotifier)
{
    public async Task<int> ApplyAsync(CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var workers = await db.Workers
            .Where(x => x.AutoScheduleEnabled && !db.WorkerScheduleAssignments.Any(a => a.WorkerId == x.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (workers.Count == 0)
        {
            return 0;
        }

        var changed = new List<WorkerEntity>();
        var officesToReset = new HashSet<Guid>();
        foreach (var worker in workers)
        {
            var shouldBeActive = WorkerScheduleRules.IsActiveNow(
                worker.AutoScheduleEnabled,
                worker.AutoScheduleDays,
                worker.AutoScheduleFromLocalTime,
                worker.AutoScheduleToLocalTime,
                nowUtc);
            var shouldBePaused = !shouldBeActive;

            if (worker.IsMonitoringPaused == shouldBePaused)
            {
                continue;
            }

            if (!shouldBePaused)
            {
                var otherRunning = await db.Workers
                    .CountAsync(
                        x => x.OfficeId == worker.OfficeId
                            && x.IsEnabled
                            && !x.IsMonitoringPaused
                            && x.Id != worker.Id,
                        ct)
                    .ConfigureAwait(false);
                if (otherRunning == 0)
                {
                    officesToReset.Add(worker.OfficeId);
                }
            }

            worker.IsMonitoringPaused = shouldBePaused;
            changed.Add(worker);
        }

        if (changed.Count == 0)
        {
            return 0;
        }

        if (officesToReset.Count > 0)
        {
            await leadExportQuota.ResetSessionsForOfficesAsync(officesToReset, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var worker in changed)
        {
            await workerPushNotifier.PushConfigChangedAsync(worker.Id, ct).ConfigureAwait(false);

            panelRealtime.Notify(
                [PanelChangeKind.Workers, PanelChangeKind.Dashboard],
                worker.OfficeId,
                worker.Id);
        }

        return changed.Count;
    }
}

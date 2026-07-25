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
            .Where(x => x.AutoScheduleEnabled)
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
            var shouldBeEnabled = WorkerScheduleRules.IsActiveNow(
                worker.AutoScheduleEnabled,
                worker.AutoScheduleDays,
                worker.AutoScheduleFromLocalTime,
                worker.AutoScheduleToLocalTime,
                nowUtc);

            if (worker.IsEnabled == shouldBeEnabled)
            {
                continue;
            }

            if (shouldBeEnabled)
            {
                var otherEnabled = await db.Workers
                    .CountAsync(x => x.OfficeId == worker.OfficeId && x.IsEnabled && x.Id != worker.Id, ct)
                    .ConfigureAwait(false);
                if (otherEnabled == 0)
                {
                    officesToReset.Add(worker.OfficeId);
                }
            }

            worker.IsEnabled = shouldBeEnabled;
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
            if (worker.IsEnabled)
            {
                await workerPushNotifier.PushConfigChangedAsync(worker.Id, ct).ConfigureAwait(false);
            }
            else
            {
                await workerPushNotifier.TryPushCommandAsync(worker.Id, WorkerCommands.Pause, ct).ConfigureAwait(false);
            }

            panelRealtime.Notify(
                [PanelChangeKind.Workers, PanelChangeKind.Dashboard],
                worker.OfficeId,
                worker.Id);
        }

        return changed.Count;
    }
}

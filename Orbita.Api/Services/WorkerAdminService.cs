using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerAdminService(
    OrbitaDbContext db,
    IConfiguration configuration,
    WorkerReleaseService releases,
    IPanelRealtimeNotifier panelRealtime,
    WorkerConnectionRegistry connectionRegistry,
    IWorkerPushNotifier workerPushNotifier,
    LeadExportQuotaService leadExportQuota)
{
    public async Task<(CreateWorkerResponse? Result, string? Error)> CreateAsync(
        string displayName,
        Guid? officeId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (null, "Имя воркера обязательно.");
        }

        if (!scope.IsGlobalAdmin)
        {
            if (!scope.OfficeId.HasValue)
            {
                return (null, "У пользователя не назначен офис.");
            }

            if (officeId.HasValue && officeId != scope.OfficeId)
            {
                return (null, "Нельзя создать воркер в другом офисе.");
            }
        }

        var resolvedOfficeId = ResolveOfficeIdForCreate(officeId, scope);
        if (resolvedOfficeId is null)
        {
            return (null, "Укажите офис для воркера.");
        }

        if (!await db.Offices.AnyAsync(x => x.Id == resolvedOfficeId && x.IsEnabled, ct))
        {
            return (null, "Офис не найден или отключён.");
        }

        var apiKey = ApiKeyService.GenerateApiKey();
        var worker = new WorkerEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = resolvedOfficeId.Value,
            DisplayName = displayName.Trim(),
            MachineName = string.Empty,
            AppVersion = string.Empty,
            ApiKeyHash = ApiKeyService.HashApiKey(apiKey),
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = null,
            MaxConcurrentAccounts = 1
        };

        db.Workers.Add(worker);
        await db.SaveChangesAsync(ct);
        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Dashboard],
            worker.OfficeId,
            worker.Id);
        return (new CreateWorkerResponse(worker.Id, apiKey, worker.DisplayName), null);
    }

    public async Task<IReadOnlyList<AdminWorkerListItemDto>> ListAsync(
        OfficeScope scope,
        Guid? officeFilter = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var latestRelease = await releases.GetLatestAsync(ct);
        var latestReleaseVersion = latestRelease?.Version;
        var workers = await db.Workers
            .AsNoTracking()
            .Include(x => x.Office)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(ct);

        return workers
            .Where(x => !LeadFlowImportWorker.IsImportWorker(x.MachineName))
            .Where(x => scope.IsGlobalAdmin
                ? officeFilter is null || x.OfficeId == officeFilter
                : x.OfficeId == scope.OfficeId)
            .Select(x => Map(x, connectionRegistry, now, latestReleaseVersion))
            .ToList();
    }

    public async Task<(AdminWorkerListItemDto? Worker, string? Error)> RenameAsync(
        Guid id,
        string displayName,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (null, "Имя воркера обязательно.");
        }

        var worker = await db.Workers.Include(x => x.Office).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (worker is null || !scope.CanAccessWorker(worker.OfficeId))
        {
            return (null, "Воркер не найден.");
        }

        worker.DisplayName = displayName.Trim();
        await db.SaveChangesAsync(ct);
        return (Map(worker, connectionRegistry), null);
    }

    public async Task<(AdminWorkerListItemDto? Worker, string? Error)> SetEnabledAsync(
        Guid id,
        bool enabled,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var worker = await db.Workers.Include(x => x.Office).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (worker is null || !scope.CanAccessWorker(worker.OfficeId))
        {
            return (null, "Воркер не найден.");
        }

        if (enabled)
        {
            var otherEnabled = await db.Workers
                .CountAsync(x => x.OfficeId == worker.OfficeId && x.IsEnabled && x.Id != id, ct)
                .ConfigureAwait(false);
            if (otherEnabled == 0)
            {
                await leadExportQuota.ResetSessionsForOfficeAsync(worker.OfficeId, ct).ConfigureAwait(false);
            }
        }

        worker.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        if (enabled)
        {
            await workerPushNotifier.PushConfigChangedAsync(worker.Id, ct).ConfigureAwait(false);
        }
        else
        {
            await workerPushNotifier.TryPushCommandAsync(worker.Id, WorkerCommands.Pause, ct)
                .ConfigureAwait(false);
        }

        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Dashboard],
            worker.OfficeId,
            worker.Id);
        return (Map(worker, connectionRegistry), null);
    }

    public async Task<(AdminWorkerListItemDto? Worker, string? Error)> SetMonitoringPausedAsync(
        Guid id,
        bool paused,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var worker = await db.Workers.Include(x => x.Office).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (worker is null || !scope.CanAccessWorker(worker.OfficeId))
        {
            return (null, "Воркер не найден.");
        }

        if (worker.IsMonitoringPaused == paused)
        {
            return (Map(worker, connectionRegistry), null);
        }

        if (!paused)
        {
            var otherRunning = await db.Workers
                .CountAsync(
                    x => x.OfficeId == worker.OfficeId
                        && x.Id != id
                        && x.IsEnabled
                        && !x.IsMonitoringPaused,
                    ct)
                .ConfigureAwait(false);
            if (otherRunning == 0)
            {
                await leadExportQuota.ResetSessionsForOfficeAsync(worker.OfficeId, ct).ConfigureAwait(false);
            }
        }

        worker.IsMonitoringPaused = paused;
        await db.SaveChangesAsync(ct);
        await workerPushNotifier.PushConfigChangedAsync(worker.Id, ct).ConfigureAwait(false);

        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Dashboard],
            worker.OfficeId,
            worker.Id);
        return (Map(worker, connectionRegistry), null);
    }

    public async Task<(BulkWorkersMonitoringResultDto? Result, string? Error)> SetAllEnabledAsync(
        bool enabled,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (!scope.HasAccess)
        {
            return (null, "Нет доступа.");
        }

        var workers = await db.Workers
            .Where(x => !LeadFlowImportWorker.IsImportWorker(x.MachineName))
            .Where(x => scope.IsGlobalAdmin || x.OfficeId == scope.OfficeId)
            .ToListAsync(ct);

        var changed = new List<WorkerEntity>();
        var officesToReset = new HashSet<Guid>();
        foreach (var worker in workers)
        {
            if (!scope.CanAccessWorker(worker.OfficeId) || worker.IsEnabled == enabled)
            {
                continue;
            }

            if (enabled
                && workers.Count(x => x.OfficeId == worker.OfficeId && x.IsEnabled) == 0)
            {
                officesToReset.Add(worker.OfficeId);
            }

            worker.IsEnabled = enabled;
            changed.Add(worker);
        }

        if (changed.Count > 0)
        {
            if (enabled && officesToReset.Count > 0)
            {
                await leadExportQuota.ResetSessionsForOfficesAsync(officesToReset, ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct);
            foreach (var worker in changed)
            {
                if (enabled)
                {
                    await workerPushNotifier.PushConfigChangedAsync(worker.Id, ct).ConfigureAwait(false);
                }
                else
                {
                    await workerPushNotifier.TryPushCommandAsync(worker.Id, WorkerCommands.Pause, ct)
                        .ConfigureAwait(false);
                }
            }

            var officeIds = changed.Select(x => x.OfficeId).Distinct().ToList();
            foreach (var officeId in officeIds)
            {
                panelRealtime.Notify(
                    [PanelChangeKind.Workers, PanelChangeKind.Dashboard],
                    officeId);
            }
        }

        return (new BulkWorkersMonitoringResultDto(changed.Count, workers.Count - changed.Count, workers.Count), null);
    }

    public async Task<(BulkWorkersMonitoringResultDto? Result, string? Error)> SetAllMonitoringPausedAsync(
        bool paused,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (!scope.HasAccess)
        {
            return (null, "Нет доступа.");
        }

        var workers = await db.Workers
            .Where(x => !LeadFlowImportWorker.IsImportWorker(x.MachineName))
            .Where(x => scope.IsGlobalAdmin || x.OfficeId == scope.OfficeId)
            .ToListAsync(ct);

        var changed = new List<WorkerEntity>();
        var officesToReset = new HashSet<Guid>();
        foreach (var worker in workers)
        {
            if (!scope.CanAccessWorker(worker.OfficeId) || worker.IsMonitoringPaused == paused)
            {
                continue;
            }

            if (!paused
                && workers.Count(x => x.OfficeId == worker.OfficeId && x.IsEnabled && !x.IsMonitoringPaused) == 0)
            {
                officesToReset.Add(worker.OfficeId);
            }

            worker.IsMonitoringPaused = paused;
            changed.Add(worker);
        }

        if (changed.Count > 0)
        {
            if (!paused && officesToReset.Count > 0)
            {
                await leadExportQuota.ResetSessionsForOfficesAsync(officesToReset, ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct);
            foreach (var worker in changed)
            {
                await workerPushNotifier.PushConfigChangedAsync(worker.Id, ct).ConfigureAwait(false);
            }

            var officeIds = changed.Select(x => x.OfficeId).Distinct().ToList();
            foreach (var officeId in officeIds)
            {
                panelRealtime.Notify(
                    [PanelChangeKind.Workers, PanelChangeKind.Dashboard],
                    officeId);
            }
        }

        return (new BulkWorkersMonitoringResultDto(changed.Count, workers.Count - changed.Count, workers.Count), null);
    }

    public async Task<(string? DisplayName, string? Error)> DeleteAsync(
        Guid id,
        OfficeScope scope,
        WorkerDiagnosticsService diagnostics,
        CancellationToken ct = default)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (worker is null || !scope.CanAccessWorker(worker.OfficeId))
        {
            return (null, "Воркер не найден.");
        }

        var displayName = worker.DisplayName;
        var officeId = worker.OfficeId;

        await db.CandidateResponses
            .Where(x => x.WorkerId == id && x.WorkerName == string.Empty)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.WorkerName, displayName),
                ct);
        await db.CandidateResponses
            .Where(x => x.WorkerId == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.WorkerId, (Guid?)null),
                ct);

        await diagnostics.DeleteAllForWorkerAsync(id, ct);
        await db.WorkerEvents.Where(x => x.WorkerId == id).ExecuteDeleteAsync(ct);
        await db.WorkerAccounts.Where(x => x.WorkerId == id).ExecuteDeleteAsync(ct);
        await db.WorkerSnapshots.Where(x => x.WorkerId == id).ExecuteDeleteAsync(ct);
        db.Workers.Remove(worker);
        await db.SaveChangesAsync(ct);
        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Dashboard],
            officeId,
            id);
        return (displayName, null);
    }

    public async Task<(RotateWorkerApiKeyResponse? Result, string? Error)> RotateApiKeyAsync(
        Guid id,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var worker = await db.Workers.FindAsync([id], ct);
        if (worker is null || !scope.CanAccessWorker(worker.OfficeId))
        {
            return (null, "Воркер не найден.");
        }

        var apiKey = ApiKeyService.GenerateApiKey();
        worker.ApiKeyHash = ApiKeyService.HashApiKey(apiKey);
        worker.ApiKeyRotatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return (new RotateWorkerApiKeyResponse(worker.Id, apiKey), null);
    }

    public WorkerRegistrationInfoDto GetRegistrationInfo()
    {
        var secret = configuration["RegistrationSecret"] ?? string.Empty;
        var isConfigured = !string.IsNullOrWhiteSpace(secret);
        return new WorkerRegistrationInfoDto(
            isConfigured,
            MaskSecret(secret),
            "legacy-config");
    }

    private static Guid? ResolveOfficeIdForCreate(Guid? officeId, OfficeScope scope) =>
        scope.IsGlobalAdmin ? officeId : scope.OfficeId;

    private static AdminWorkerListItemDto Map(
        WorkerEntity worker,
        WorkerConnectionRegistry registry,
        DateTime? nowUtc = null,
        string? latestReleaseVersion = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var updateAvailable = AppVersionHelper.IsNewer(latestReleaseVersion, worker.AppVersion);
        return new AdminWorkerListItemDto(
            worker.Id,
            worker.DisplayName,
            worker.MachineName,
            worker.AppVersion,
            worker.IsEnabled,
            WorkerOnlineRules.IsOnline(worker.LastSeenAtUtc, now, registry.IsConnected(worker.Id)),
            worker.LastSeenAtUtc,
            worker.CreatedAtUtc,
            worker.ApiKeyRotatedAtUtc,
            updateAvailable,
            latestReleaseVersion,
            worker.OfficeId,
            worker.Office?.Name ?? string.Empty,
            worker.IsMonitoringPaused);
    }

    private static string MaskSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return "—";
        }

        return secret.Length <= 4 ? "****" : $"****{secret[^4..]}";
    }
}

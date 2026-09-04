using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;
using Orbita.Logging.Audit;

namespace Orbita.Api.Services;

public sealed class TelemetryService(
    OrbitaDbContext db,
    OfficeAdminService offices,
    IPanelRealtimeNotifier panelRealtime)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WorkerRegisterResponse?> RegisterAsync(
        WorkerRegisterRequest request,
        string? legacyRegistrationSecret,
        CancellationToken ct)
    {
        var office = await offices.FindByRegistrationSecretAsync(request.RegistrationSecret, ct);
        if (office is null
            && (string.IsNullOrWhiteSpace(legacyRegistrationSecret)
                || !string.Equals(request.RegistrationSecret, legacyRegistrationSecret, StringComparison.Ordinal)))
        {
            return null;
        }

        office ??= await offices.EnsureDefaultOfficeAsync(legacyRegistrationSecret, ct);

        var apiKey = ApiKeyService.GenerateApiKey();
        var worker = new WorkerEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = office.Id,
            DisplayName = request.DisplayName.Trim(),
            MachineName = request.MachineName.Trim(),
            AppVersion = request.AppVersion.Trim(),
            ApiKeyHash = ApiKeyService.HashApiKey(apiKey),
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow
        };

        db.Workers.Add(worker);
        await db.SaveChangesAsync(ct);
        return new WorkerRegisterResponse(worker.Id, apiKey);
    }

    public async Task<bool> HeartbeatAsync(WorkerHeartbeatRequest request, string? clientIpAddress, CancellationToken ct)
    {
        var worker = await db.Workers.FindAsync([request.WorkerId], ct);
        if (worker is null)
        {
            return false;
        }

        var nowUtc = DateTime.UtcNow;
        var wasOnline = WorkerOnlineRules.IsOnline(worker.LastSeenAtUtc, nowUtc);
        var previousMonitoringStatus = worker.MonitoringStatus;

        worker.MachineName = request.MachineName.Trim();
        if (string.IsNullOrWhiteSpace(worker.DisplayName))
        {
            worker.DisplayName = request.DisplayName.Trim();
        }
        worker.AppVersion = request.AppVersion.Trim();
        worker.MonitoringStatus = request.MonitoringStatus;
        worker.MonitoringStatusMessage = request.MonitoringStatusMessage;
        worker.IsMonitoringActive = request.IsMonitoringActive;
        worker.NextCycleCheckAtUtc = DateTimeUtcHelper.EnsureUtc(request.NextCycleCheckAtUtc);
        worker.LastSeenAtUtc = DateTime.UtcNow;

        var ipAddress = WorkerIpAddressRules.ResolveForHeartbeat(clientIpAddress, request.PublicIpAddress);
        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            worker.IpAddress = ipAddress;
        }

        if (!string.IsNullOrWhiteSpace(request.OperatingSystem))
        {
            worker.OperatingSystem = request.OperatingSystem.Trim();
        }

        if (request.StartedAtUtc is not null)
        {
            worker.StartedAtUtc = DateTimeUtcHelper.EnsureUtc(request.StartedAtUtc);
        }

        if (!string.IsNullOrWhiteSpace(request.AgentVersion))
        {
            worker.AgentVersion = request.AgentVersion.Trim();
        }
        if (request.SystemMetrics is not null)
        {
            worker.LastCpuPercent = request.SystemMetrics.CpuPercent;
            worker.LastRamPercent = request.SystemMetrics.RamPercent;
            worker.LastRamUsedMb = request.SystemMetrics.RamUsedMb;
            worker.LastRamTotalMb = request.SystemMetrics.RamTotalMb;
        }

        if (request.LastUpdateResult is not null)
        {
            worker.LastUpdateVersion = request.LastUpdateResult.Version;
            worker.LastUpdateSuccess = request.LastUpdateResult.Success;
            worker.LastUpdateMessage = request.LastUpdateResult.Message;
            worker.LastUpdateAtUtc = DateTimeUtcHelper.EnsureUtc(request.LastUpdateResult.CompletedAtUtc);
        }

        await db.SaveChangesAsync(ct);

        var isOnline = WorkerOnlineRules.IsOnline(worker.LastSeenAtUtc, nowUtc);
        if (wasOnline != isOnline || previousMonitoringStatus != worker.MonitoringStatus)
        {
            panelRealtime.Notify(
                [PanelChangeKind.Workers],
                worker.OfficeId,
                worker.Id);
        }

        return true;
    }

    public async Task<bool> SaveActivityAsync(WorkerActivityRequest request, CancellationToken ct)
    {
        var worker = await db.Workers.FindAsync([request.WorkerId], ct);
        if (worker is null)
        {
            return false;
        }

        var changed = WorkerActivityMapper.ActivityChanged(worker, request);
        WorkerActivityMapper.Apply(worker, request);
        worker.LastSeenAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (changed)
        {
            panelRealtime.Notify(
                [PanelChangeKind.Workers],
                worker.OfficeId,
                worker.Id);
        }

        return true;
    }

    public async Task<bool> SaveSnapshotAsync(WorkerSnapshotRequest request, CancellationToken ct)
    {
        var worker = await db.Workers.FindAsync([request.WorkerId], ct);
        if (worker is null)
        {
            return false;
        }

        worker.LastSeenAtUtc = DateTime.UtcNow;
        worker.MonitoringStatus = worker.MonitoringStatus;

        // Slim: if the worker sent a mostly-empty snapshot, only update LastSeen (heartbeat already does the heavy lifting).
        if (IsMostlyEmptySnapshot(request))
        {
            await db.SaveChangesAsync(ct);
            return true;
        }

        var capturedAtUtc = DateTimeUtcHelper.EnsureUtc(request.CapturedAtUtc);

        var existingAccounts = await db.WorkerAccounts
            .Where(x => x.WorkerId == request.WorkerId)
            .ToDictionaryAsync(x => x.AccountId, ct);

        var previousBalances = await db.WorkerSnapshots
            .AsNoTracking()
            .Where(x => x.WorkerId == request.WorkerId)
            .OrderByDescending(x => x.CapturedAtUtc)
            .Select(x => x.BalancesJson)
            .FirstOrDefaultAsync(ct);

        var previousBalanceItems = string.IsNullOrWhiteSpace(previousBalances)
            ? []
            : JsonSerializer.Deserialize<List<WorkerBalanceDto>>(previousBalances, JsonOptions) ?? [];

        var mergedBalances = BalanceSnapshotHelper.MergeWithPersisted(
            request.Balances,
            previousBalanceItems,
            existingAccounts,
            request.Accounts);

        var balanceByAccount = mergedBalances.ToDictionary(x => x.AccountId);

        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = request.WorkerId,
            CapturedAtUtc = capturedAtUtc,
            StatsJson = JsonSerializer.Serialize(request.Stats, JsonOptions),
            BalancesJson = JsonSerializer.Serialize(mergedBalances, JsonOptions)
        });

        foreach (var account in request.Accounts)
        {
            balanceByAccount.TryGetValue(account.AccountId, out var balanceDto);
            var balance = balanceDto?.TotalBalance ?? 0m;
            if (existingAccounts.TryGetValue(account.AccountId, out var existing))
            {
                ApplyAccountSnapshot(existing, account, balance, balanceDto, capturedAtUtc);
            }
            else
            {
                var created = new WorkerAccountEntity
                {
                    WorkerId = request.WorkerId,
                    AccountId = account.AccountId
                };
                ApplyAccountSnapshot(created, account, balance, balanceDto, capturedAtUtc);
                db.WorkerAccounts.Add(created);
            }
        }

        await SaveSnapshotChangesAsync(request.WorkerId, ct);

        if (!IsMostlyEmptySnapshot(request))
        {
            panelRealtime.Notify(
                [PanelChangeKind.Workers],
                worker.OfficeId,
                worker.Id);
        }

        // Probabilistic retention to keep snapshot table from growing unbounded.
        // Keep latest + anything in last ~48h. Called rarely to avoid overhead.
        if (Random.Shared.Next(0, 25) == 0)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-48);
                var stale = await db.WorkerSnapshots
                    .Where(s => s.WorkerId == request.WorkerId && s.CapturedAtUtc < cutoff)
                    .ToListAsync(ct);
                if (stale.Count > 0)
                {
                    db.WorkerSnapshots.RemoveRange(stale);
                    await db.SaveChangesAsync(ct);
                }
            }
            catch
            {
                // ignore prune errors
            }
        }

        return true;
    }

    private static void ApplyAccountSnapshot(
        WorkerAccountEntity target,
        WorkerAccountDto account,
        decimal balance,
        WorkerBalanceDto? balanceDto,
        DateTime capturedAtUtc)
    {
        target.DisplayName = account.DisplayName;
        target.Status = account.Status;
        target.IsEnabled = account.IsEnabled;
        target.IsEnabledInPanel = account.IsEnabledInPanel;
        target.AdsPowerProfileId = account.AdsPowerProfileId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(account.MultiloginProfileId))
        {
            target.MultiloginProfileId = account.MultiloginProfileId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(account.MultiloginFolderId))
        {
            target.MultiloginFolderId = account.MultiloginFolderId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(account.LocalUserDataDir))
        {
            target.LocalUserDataDir = account.LocalUserDataDir.Trim();
            target.LocalTrafficLastNavigationMs = account.LocalTrafficLastNavigationMs;
            target.LocalTrafficBlockedMedia = account.LocalTrafficBlockedMedia;
            target.LocalTrafficBlockedImages = account.LocalTrafficBlockedImages;
            target.LocalTrafficBlockedFonts = account.LocalTrafficBlockedFonts;
            target.LocalTrafficBlockedAnalytics = account.LocalTrafficBlockedAnalytics;
            target.LocalTrafficBlockedPrefetch = account.LocalTrafficBlockedPrefetch;
        }

        target.ActiveAdsCount = account.ActiveAdsCount;
        target.BlockedCount = account.BlockedCount;
        target.DraftsCount = account.DraftsCount;
        target.LastErrorMessage = account.LastErrorMessage;
        target.LastMonitoringAt = DateTimeUtcHelper.EnsureUtc(account.LastMonitoringAt);
        target.TotalBalance = BalanceSnapshotHelper.ResolveTotalBalance(
            balance,
            target,
            balanceDto,
            account);

        if (SubProfileSnapshotHelper.MergeForPersist(account.SubProfiles, target.SubProfilesJson) is
            { } mergedSubProfiles)
        {
            target.SubProfilesJson = SerializeSubProfiles(mergedSubProfiles);
        }

        var refreshedAtUtc = DateTimeUtcHelper.EnsureUtc(account.SubProfilesRefreshedAtUtc);
        if (refreshedAtUtc is not null)
        {
            target.SubProfilesRefreshedAtUtc = refreshedAtUtc;
        }

        if (target.SubProfilesRefreshRequestedAtUtc is not null
            && refreshedAtUtc is not null
            && refreshedAtUtc >= target.SubProfilesRefreshRequestedAtUtc)
        {
            target.SubProfilesRefreshRequestedAtUtc = null;
        }

        target.UpdatedAtUtc = capturedAtUtc;
    }

    private async Task SaveSnapshotChangesAsync(Guid workerId, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            var inner = ex.InnerException?.Message ?? ex.Message;
            _ = GlobalLogger.Instance.LogAsync(
                $"Telemetry snapshot save failed for worker {workerId}: {inner}",
                DeskLinkAuditLogLevel.Error,
                errorKey: "telemetry.snapshot.save_failed",
                properties: new Dictionary<string, object?>
                {
                    ["workerId"] = workerId,
                    ["exception"] = ex.GetType().Name
                });
            throw;
        }
    }

    private static string SerializeSubProfiles(IReadOnlyList<WorkerSubProfileDto>? subProfiles) =>
        JsonSerializer.Serialize(subProfiles ?? [], SubProfileJsonOptions.Serialize);

    private static bool IsMostlyEmptySnapshot(WorkerSnapshotRequest req)
    {
        var s = req.Stats;
        bool statsEmpty = s.NewResponses == 0 && s.TotalToday == 0 && s.SentToCrm == 0 &&
                          s.Duplicates == 0 && s.Errors == 0 && s.ActiveAdsCount == 0 &&
                          (s.HourlyActivity == null || s.HourlyActivity.All(p => p.NewCount == 0));
        bool noBalances = req.Balances == null || req.Balances.Count == 0;
        bool accountsTrivial = req.Accounts == null || req.Accounts.All(a => a.ActiveAdsCount == 0 && a.BlockedCount == 0 && string.IsNullOrEmpty(a.LastErrorMessage));
        return statsEmpty && noBalances && accountsTrivial;
    }

    public async Task<bool> SaveEventsAsync(WorkerEventBatchRequest request, CancellationToken ct)
    {
        var exists = await db.Workers.AnyAsync(x => x.Id == request.WorkerId, ct);
        if (!exists)
        {
            return false;
        }

        if (request.Events.Count == 0)
        {
            await SaveSnapshotChangesAsync(request.WorkerId, ct);
            return true;
        }

        var messages = request.Events
            .Select(x => x.Message.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existingRows = messages.Count == 0
            ? []
            : await db.WorkerEvents
                .AsNoTracking()
                .Where(x => x.WorkerId == request.WorkerId && messages.Contains(x.Message))
                .Select(x => new WorkerEventFingerprintRow(
                    x.AccountId,
                    x.Level,
                    x.Message,
                    x.IsDismissed))
                .ToListAsync(ct);

        var knownFingerprints = existingRows
            .Select(x => WorkerEventDedupHelper.BuildFingerprint(
                request.WorkerId,
                x.AccountId,
                x.Level,
                x.Message))
            .ToHashSet(StringComparer.Ordinal);

        var inserted = 0;
        foreach (var evt in request.Events)
        {
            if (string.IsNullOrWhiteSpace(evt.Message))
            {
                continue;
            }

            var fingerprint = WorkerEventDedupHelper.BuildFingerprint(
                request.WorkerId,
                evt.AccountId,
                evt.Level,
                evt.Message);
            if (!knownFingerprints.Add(fingerprint))
            {
                continue;
            }

            db.WorkerEvents.Add(new WorkerEventEntity
            {
                Id = Guid.NewGuid(),
                WorkerId = request.WorkerId,
                AccountId = evt.AccountId,
                Level = evt.Level,
                Message = evt.Message,
                Details = evt.Details,
                CreatedAtUtc = DateTimeUtcHelper.EnsureUtc(evt.CreatedAtUtc)
            });
            inserted++;
        }

        await SaveSnapshotChangesAsync(request.WorkerId, ct);

        if (inserted > 0)
        {
            var worker = await db.Workers.AsNoTracking()
                .Where(x => x.Id == request.WorkerId)
                .Select(x => new { x.OfficeId })
                .FirstOrDefaultAsync(ct);

            if (worker is not null)
            {
                panelRealtime.Notify(
                    [
                        PanelChangeKind.Events,
                        PanelChangeKind.Errors
                    ],
                    worker.OfficeId,
                    request.WorkerId);
            }
        }

        return true;
    }

    private sealed record WorkerEventFingerprintRow(
        Guid? AccountId,
        string Level,
        string Message,
        bool IsDismissed);
}

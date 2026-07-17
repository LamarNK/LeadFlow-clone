using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

using static Orbita.Api.Helpers.SubProfilesDisabledIdsHelper;

namespace Orbita.Api.Services;

public sealed class WorkerConfigService(
    OrbitaDbContext db,
    OfficeScopeService officeScope,
    IPanelRealtimeNotifier panelRealtime,
    IWorkerPushNotifier workerPushNotifier,
    WorkerReleaseService releases,
    CaptchaSessionService captchaSessions,
    BrowserMonitorService browserMonitorSessions)
{
    public Task<WorkerConfigDto?> GetConfigForWorkerAsync(
        Guid workerId,
        OfficeScope scope,
        CancellationToken ct = default) =>
        GetConfigForWorkerAsync(workerId, scope, consumePendingCommand: false, ct);

    public async Task<WorkerConfigDto?> GetConfigForWorkerAsync(
        Guid workerId,
        OfficeScope scope,
        bool consumePendingCommand,
        CancellationToken ct = default)
    {
        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return null;
        }

        var worker = consumePendingCommand
            ? await db.Workers.FirstOrDefaultAsync(x => x.Id == workerId, ct)
            : await db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == workerId, ct);
        if (worker is null)
        {
            return null;
        }

        var pendingCommand = worker.PendingCommand;
        if (consumePendingCommand && !string.IsNullOrWhiteSpace(pendingCommand))
        {
            worker.PendingCommand = null;
            worker.PendingCommandAtUtc = null;
            await db.SaveChangesAsync(ct);
        }

        var accountRows = await db.WorkerAccounts
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(ct);

        var accounts = accountRows
            .Select(x => ToAccountConfigDto(x, worker.AdsPowerApiBaseUrl, worker.AdsPowerApiKey))
            .ToList();

        var updateCheck = await releases.CheckUpdateAsync(worker.AppVersion, ct);
        WorkerUpdateOfferDto? updateOffer = null;
        if (updateCheck.HasUpdate
            && !string.IsNullOrWhiteSpace(updateCheck.LatestVersion)
            && !string.IsNullOrWhiteSpace(updateCheck.DownloadPath)
            && !string.IsNullOrWhiteSpace(updateCheck.Sha256))
        {
            updateOffer = new WorkerUpdateOfferDto(
                updateCheck.LatestVersion,
                updateCheck.DownloadPath,
                updateCheck.Sha256,
                updateCheck.FileSize,
                updateCheck.ReleaseNotes);
        }

        var pendingCaptcha = await captchaSessions
            .GetPendingForWorkerAsync(worker.Id, ct)
            .ConfigureAwait(false);

        var pendingBrowserMonitor = await browserMonitorSessions
            .GetPendingForWorkerAsync(worker.Id, ct)
            .ConfigureAwait(false);

        return new WorkerConfigDto(
            worker.Id,
            worker.MaxConcurrentAccounts,
            worker.AdsPowerApiBaseUrl,
            worker.AdsPowerApiKey,
            accounts,
            pendingCommand,
            updateOffer,
            pendingCaptcha,
            pendingBrowserMonitor,
            worker.ResponseFilterEnabled,
            worker.ResponseFilterExcludeFemale,
            worker.ResponseFilterMaxAge);
    }

    public async Task<bool> SyncAccountsAsync(
        Guid workerId,
        WorkerAccountSyncRequest request,
        CancellationToken ct = default)
    {
        var worker = await db.Workers.FindAsync([workerId], ct);
        if (worker is null)
        {
            return false;
        }

        worker.LastSeenAtUtc = DateTime.UtcNow;
        var existing = await db.WorkerAccounts
            .Where(x => x.WorkerId == workerId)
            .ToDictionaryAsync(x => x.AccountId, ct);

        var syncedAccountIds = new HashSet<Guid>();
        var now = DateTime.UtcNow;

        foreach (var item in request.Accounts)
        {
            if (string.IsNullOrWhiteSpace(item.AdsPowerProfileId))
            {
                continue;
            }

            var accountId = AdsPowerAccountId.ToAccountGuid(item.AdsPowerProfileId);
            syncedAccountIds.Add(accountId);

            if (existing.TryGetValue(accountId, out var account))
            {
                account.AdsPowerProfileId = item.AdsPowerProfileId.Trim();
                account.DisplayName = item.DisplayName.Trim();
                account.UpdatedAtUtc = now;
            }
            else
            {
                db.WorkerAccounts.Add(new WorkerAccountEntity
                {
                    WorkerId = workerId,
                    AccountId = accountId,
                    AdsPowerProfileId = item.AdsPowerProfileId.Trim(),
                    DisplayName = item.DisplayName.Trim(),
                    Status = string.Empty,
                    IsEnabled = false,
                    IsEnabledInPanel = false,
                    UpdatedAtUtc = now
                });
            }
        }

        foreach (var stale in existing.Values.Where(x => !syncedAccountIds.Contains(x.AccountId)))
        {
            db.WorkerAccounts.Remove(stale);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(WorkerConfigDto? Config, string? Error)> UpdateSettingsAsync(
        Guid workerId,
        UpdateWorkerSettingsRequest request,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (request.MaxConcurrentAccounts < 1)
        {
            return (null, "MaxConcurrentAccounts должен быть не меньше 1.");
        }

        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return (null, "Воркер не найден.");
        }

        var worker = await db.Workers.FindAsync([workerId], ct);
        if (worker is null)
        {
            return (null, "Воркер не найден.");
        }

        if (!TryNormalizeAdsPowerApiBaseUrl(request.AdsPowerApiBaseUrl, out var normalizedBaseUrl, out var baseUrlError))
        {
            return (null, baseUrlError);
        }

        if (!TryNormalizeAdsPowerApiKey(request.AdsPowerApiKey, out var normalizedApiKey, out var apiKeyError))
        {
            return (null, apiKeyError);
        }

        var filters = ResponseCollectionFilters.Normalize(
            request.ResponseFilterEnabled,
            request.ResponseFilterExcludeFemale,
            request.ResponseFilterMaxAge);

        worker.MaxConcurrentAccounts = request.MaxConcurrentAccounts;
        worker.AdsPowerApiBaseUrl = normalizedBaseUrl;
        worker.AdsPowerApiKey = normalizedApiKey;
        worker.ResponseFilterEnabled = filters.Enabled;
        worker.ResponseFilterExcludeFemale = filters.ExcludeFemale;
        worker.ResponseFilterMaxAge = filters.MaxAgeInclusive;
        await db.SaveChangesAsync(ct);
        await workerPushNotifier.PushConfigChangedAsync(workerId, ct).ConfigureAwait(false);
        return (await GetConfigForWorkerAsync(workerId, scope, ct), null);
    }

    private static bool TryNormalizeAdsPowerApiBaseUrl(
        string? value,
        out string? normalized,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = null;
            error = null;
            return true;
        }

        var trimmed = value.Trim().TrimEnd('/');
        if (trimmed.Length > 512)
        {
            normalized = null;
            error = "URL AdsPower API не должен превышать 512 символов.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            normalized = null;
            error = "Укажите корректный URL AdsPower API (http или https).";
            return false;
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    private static bool TryNormalizeAdsPowerApiKey(
        string? value,
        out string? normalized,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = null;
            error = null;
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 256)
        {
            normalized = null;
            error = "API Key AdsPower не должен превышать 256 символов.";
            return false;
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    public async Task<(WorkerAccountConfigDto? Account, string? Error)> UpdateAccountEnabledAsync(
        Guid workerId,
        Guid accountId,
        UpdateWorkerAccountRequest request,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return (null, "Воркер не найден.");
        }

        var worker = await db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == workerId, ct);
        if (worker is null)
        {
            return (null, "Воркер не найден.");
        }

        var account = await db.WorkerAccounts.FirstOrDefaultAsync(
            x => x.WorkerId == workerId && x.AccountId == accountId,
            ct);
        if (account is null)
        {
            return (null, "Аккаунт не найден.");
        }

        account.IsEnabledInPanel = request.IsEnabledInPanel;
        if (request.IsEnabledInPanel
            && string.Equals(account.Status, "Paused", StringComparison.OrdinalIgnoreCase))
        {
            account.Status = "Active";
            account.LastErrorMessage = string.Empty;
        }

        account.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Accounts, PanelChangeKind.Dashboard],
            worker.OfficeId,
            workerId);
        await workerPushNotifier.PushConfigChangedAsync(workerId, ct).ConfigureAwait(false);

        return (ToAccountConfigDto(account, worker.AdsPowerApiBaseUrl, worker.AdsPowerApiKey), null);
    }

    private static WorkerAccountConfigDto ToAccountConfigDto(
        WorkerAccountEntity account,
        string? adsPowerApiBaseUrl,
        string? adsPowerApiKey) =>
        new(
            account.AccountId,
            account.AdsPowerProfileId,
            account.DisplayName,
            account.IsEnabledInPanel,
            adsPowerApiBaseUrl,
            adsPowerApiKey,
            account.SubProfilesRefreshRequestedAtUtc,
            Parse(account.SubProfilesDisabledIdsJson),
            string.IsNullOrWhiteSpace(account.Status) ? null : account.Status,
            string.IsNullOrWhiteSpace(account.LastErrorMessage) ? null : account.LastErrorMessage,
            account.LastMonitoringAt,
            LastAuthCheckAtUtc: null,
            string.IsNullOrWhiteSpace(account.SubProfilesJson) ? null : account.SubProfilesJson,
            account.SubProfilesRefreshedAtUtc,
            account.ActiveAdsCount,
            account.BlockedCount,
            account.DraftsCount);

    public async Task<(bool Success, string? Error)> UpdateSubProfileEnabledAsync(
        Guid workerId,
        Guid accountId,
        string subProfileId,
        UpdateWorkerSubProfileRequest request,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return (false, "Не указан субпрофиль.");
        }

        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return (false, "Воркер не найден.");
        }

        var account = await db.WorkerAccounts.FirstOrDefaultAsync(
            x => x.WorkerId == workerId && x.AccountId == accountId,
            ct);
        if (account is null)
        {
            return (false, "Аккаунт не найден.");
        }

        if (!ContainsSubProfile(account.SubProfilesJson, subProfileId.Trim()))
        {
            return (false, "Субпрофиль не найден у аккаунта.");
        }

        var disabled = Parse(account.SubProfilesDisabledIdsJson).ToList();
        var id = subProfileId.Trim();

        if (request.IsEnabledInPanel)
        {
            disabled.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal));
        }
        else if (!disabled.Contains(id, StringComparer.Ordinal))
        {
            disabled.Add(id);
        }

        account.SubProfilesDisabledIdsJson = Serialize(disabled);
        account.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var worker = await db.Workers.AsNoTracking()
            .Where(x => x.Id == workerId)
            .Select(x => new { x.OfficeId })
            .FirstOrDefaultAsync(ct);
        if (worker is not null)
        {
            panelRealtime.Notify(
                [PanelChangeKind.Workers, PanelChangeKind.Accounts],
                worker.OfficeId,
                workerId);
            await workerPushNotifier.PushConfigChangedAsync(workerId, ct).ConfigureAwait(false);
        }

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> RequestSubProfilesRefreshAsync(
        Guid workerId,
        Guid accountId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return (false, "Воркер не найден.");
        }

        var account = await db.WorkerAccounts.FirstOrDefaultAsync(
            x => x.WorkerId == workerId && x.AccountId == accountId,
            ct);
        if (account is null)
        {
            return (false, "Аккаунт не найден.");
        }

        if (string.IsNullOrWhiteSpace(account.AdsPowerProfileId))
        {
            return (false, "Аккаунт не привязан к AdsPower.");
        }

        account.SubProfilesRefreshRequestedAtUtc = DateTime.UtcNow;
        account.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var worker = await db.Workers.AsNoTracking()
            .Where(x => x.Id == workerId)
            .Select(x => new { x.OfficeId })
            .FirstOrDefaultAsync(ct);
        if (worker is not null)
        {
            panelRealtime.Notify(
                [PanelChangeKind.Workers, PanelChangeKind.Accounts],
                worker.OfficeId,
                workerId);
            await workerPushNotifier.PushConfigChangedAsync(workerId, ct).ConfigureAwait(false);
        }

        return (true, null);
    }
}
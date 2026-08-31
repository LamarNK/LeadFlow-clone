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
    BrowserMonitorService browserMonitorSessions,
    AvitoAccountSecretProtector avitoSecrets,
    IMultiloginAutomationTokenIssuer? multiloginTokenIssuer = null,
    LocalChromeLoginSessionService? localChromeLoginSessions = null)
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
            .Select(x => ToAccountConfigDto(x, worker.AdsPowerApiBaseUrl, worker.AdsPowerApiKey, includeCredentials: true))
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

        var pendingLocalChromeLogin = localChromeLoginSessions is null
            ? null
            : localChromeLoginSessions.GetPendingForWorker(worker.Id);

        var configuredParallelism = worker.MaxConcurrentAccounts;
        var ramBasedParallelism = WorkerParallelismRules.GetMaximumConcurrentAccounts(worker.LastRamTotalMb);
        var effectiveParallelism = ramBasedParallelism is null
            ? configuredParallelism
            : Math.Min(configuredParallelism, ramBasedParallelism.Value);

        return new WorkerConfigDto(
            worker.Id,
            effectiveParallelism,
            worker.AdsPowerApiBaseUrl,
            worker.AdsPowerApiKey,
            accounts,
            pendingCommand,
            updateOffer,
            pendingCaptcha,
            pendingBrowserMonitor,
            worker.ResponseFilterEnabled,
            worker.ResponseFilterExcludeFemale,
            worker.ResponseFilterMaxAge,
            worker.ResponseFilterExcludeMale,
            worker.ResponseFilterMaxAgeMale,
            worker.ResponseFilterMaxAgeFemale,
            worker.ResponseFilterMaxAgeDays,
            worker.ResponseHighlightEnabled,
            worker.ResponseHighlightAgeBuckets,
            worker.AutoScheduleEnabled,
            worker.AutoScheduleDays,
            worker.AutoScheduleFromLocalTime,
            worker.AutoScheduleToLocalTime,
            worker.MessengerAutoReplyEnabled,
            worker.MessengerAutoReplyMessage,
            worker.PhoneUnchangedHours,
            worker.ResponseHighlightTargetsJson,
            worker.AdsPowerGroupId,
            worker.RuCaptchaApiKey,
            worker.MultiloginLauncherUrl,
            worker.MultiloginAutomationToken,
            worker.MultiloginCloudApiUrl,
            worker.LocalChromeExecutablePath,
            worker.AdsPowerEnabled,
            worker.MultiloginEnabled,
            worker.LocalChromeEnabled,
            ToPendingCheck(worker),
            ToPendingSync(worker),
            pendingLocalChromeLogin);
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

        var adsPowerSyncedIds = new HashSet<Guid>();
        var multiloginSyncedIds = new HashSet<Guid>();
        var sawAdsPowerItems = false;
        var hasMultiloginMarker = false;
        var now = DateTime.UtcNow;

        foreach (var item in request.Accounts)
        {
            if (!string.IsNullOrWhiteSpace(item.AdsPowerProfileId))
            {
                sawAdsPowerItems = true;
                var accountId = AdsPowerAccountId.ToAccountGuid(item.AdsPowerProfileId);
                adsPowerSyncedIds.Add(accountId);

                if (existing.TryGetValue(accountId, out var account))
                {
                    account.AdsPowerProfileId = item.AdsPowerProfileId.Trim();
                    account.DisplayName = item.DisplayName.Trim();
                    account.AdsPowerGroupId = AdsPowerGroupsJson.NormalizeGroupId(item.AdsPowerGroupId);
                    account.AdsPowerGroupName = AdsPowerGroupsJson.NormalizeGroupName(item.AdsPowerGroupName);
                    account.UpdatedAtUtc = now;
                }
                else
                {
                    var created = new WorkerAccountEntity
                    {
                        WorkerId = workerId,
                        AccountId = accountId,
                        AdsPowerProfileId = item.AdsPowerProfileId.Trim(),
                        DisplayName = item.DisplayName.Trim(),
                        AdsPowerGroupId = AdsPowerGroupsJson.NormalizeGroupId(item.AdsPowerGroupId),
                        AdsPowerGroupName = AdsPowerGroupsJson.NormalizeGroupName(item.AdsPowerGroupName),
                        Status = string.Empty,
                        IsEnabled = false,
                        IsEnabledInPanel = false,
                        UpdatedAtUtc = now
                    };
                    db.WorkerAccounts.Add(created);
                    existing[accountId] = created;
                }
            }

            if (!string.IsNullOrWhiteSpace(item.MultiloginProfileId))
            {
                hasMultiloginMarker = true;
            }

            if (!string.IsNullOrWhiteSpace(item.MultiloginProfileId)
                && !string.IsNullOrWhiteSpace(item.MultiloginFolderId))
            {
                var profileId = item.MultiloginProfileId.Trim();
                var folderId = item.MultiloginFolderId.Trim();
                var accountId = MultiloginAccountId.ToAccountGuid(profileId);
                multiloginSyncedIds.Add(accountId);
                var displayName = FirstNonEmpty(
                    item.MultiloginProfileName,
                    item.DisplayName,
                    profileId);

                if (existing.TryGetValue(accountId, out var account))
                {
                    account.MultiloginProfileId = profileId;
                    account.MultiloginFolderId = folderId;
                    account.MultiloginProfileName = NullIfWhiteSpace(item.MultiloginProfileName) ?? displayName;
                    account.DisplayName = displayName;
                    account.UpdatedAtUtc = now;
                }
                else
                {
                    var created = new WorkerAccountEntity
                    {
                        WorkerId = workerId,
                        AccountId = accountId,
                        AdsPowerProfileId = string.Empty,
                        MultiloginProfileId = profileId,
                        MultiloginFolderId = folderId,
                        MultiloginProfileName = NullIfWhiteSpace(item.MultiloginProfileName) ?? displayName,
                        DisplayName = displayName,
                        Status = string.Empty,
                        IsEnabled = false,
                        IsEnabledInPanel = false,
                        UpdatedAtUtc = now
                    };
                    db.WorkerAccounts.Add(created);
                    existing[accountId] = created;
                }
            }
        }

        if (sawAdsPowerItems || (!hasMultiloginMarker && !request.Multilogin && !request.ReplaceMultiloginCatalog))
        {
            foreach (var stale in existing.Values.Where(x =>
                         !adsPowerSyncedIds.Contains(x.AccountId)
                         && string.IsNullOrWhiteSpace(x.MultiloginProfileId)
                         && !IsLocalAccount(x)))
            {
                db.WorkerAccounts.Remove(stale);
            }
        }

        if (request.ReplaceMultiloginCatalog)
        {
            foreach (var stale in existing.Values.Where(x =>
                         !multiloginSyncedIds.Contains(x.AccountId)
                         && !string.IsNullOrWhiteSpace(x.MultiloginProfileId)))
            {
                db.WorkerAccounts.Remove(stale);
            }
        }

        if (request.Groups is not null)
        {
            var groups = AdsPowerGroupsJson.Parse(AdsPowerGroupsJson.Serialize(request.Groups));
            worker.AdsPowerGroupsJson = AdsPowerGroupsJson.Serialize(groups);
            if (!string.IsNullOrWhiteSpace(worker.AdsPowerGroupId))
            {
                worker.AdsPowerGroupName =
                    AdsPowerGroupsJson.ResolveGroupName(worker.AdsPowerGroupId, groups)
                    ?? worker.AdsPowerGroupName;
            }
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

        var maximumConcurrentAccounts = WorkerParallelismRules.GetMaximumConcurrentAccounts(worker.LastRamTotalMb);
        if (maximumConcurrentAccounts is not null && request.MaxConcurrentAccounts > maximumConcurrentAccounts)
        {
            return (null,
                $"Для этого воркера доступно не более {maximumConcurrentAccounts} параллельных браузеров " +
                $"({WorkerParallelismRules.RamMbPerBrowser} МБ ОЗУ на браузер).");
        }

        if (!TryNormalizeAdsPowerApiKey(request.RuCaptchaApiKey, out var normalizedRuCaptchaKey, out var ruCaptchaKeyError))
        {
            return (null, ruCaptchaKeyError is null
                ? null
                : ruCaptchaKeyError.Replace("AdsPower", "RuCaptcha", StringComparison.Ordinal));
        }

        string? normalizedBaseUrl = null;
        string? normalizedApiKey = null;
        if (request.AdsPowerEnabled)
        {
            if (!TryNormalizeAdsPowerApiBaseUrl(request.AdsPowerApiBaseUrl, out normalizedBaseUrl, out var baseUrlError))
            {
                return (null, baseUrlError);
            }

            if (!TryNormalizeAdsPowerApiKey(request.AdsPowerApiKey, out normalizedApiKey, out var apiKeyError))
            {
                return (null, apiKeyError);
            }
        }

        string? normalizedLauncherUrl = null;
        string? normalizedCloudUrl = null;
        string? normalizedToken = null;
        if (request.MultiloginEnabled)
        {
            if (!TryNormalizeMultiloginUrl(
                    request.MultiloginLauncherUrl,
                    MultiloginWorkerSettings.DefaultLauncherUrl,
                    persistDefaultWhenEmpty: true,
                    "URL launcher Multilogin",
                    out normalizedLauncherUrl,
                    out var launcherError))
            {
                return (null, launcherError);
            }

            if (!TryNormalizeMultiloginUrl(
                    request.MultiloginCloudApiUrl,
                    MultiloginWorkerSettings.DefaultCloudApiUrl,
                    persistDefaultWhenEmpty: false,
                    "URL cloud API Multilogin",
                    out normalizedCloudUrl,
                    out var cloudError))
            {
                return (null, cloudError);
            }

            if (!TryNormalizeMultiloginAutomationToken(request.MultiloginAutomationToken, out var apiToken, out var tokenError))
            {
                return (null, tokenError);
            }

            if (apiToken is not null)
            {
                if (multiloginTokenIssuer is null)
                {
                    return (null, "Multilogin automation token service недоступен.");
                }

                try
                {
                    normalizedToken = await multiloginTokenIssuer
                        .IssueAsync(normalizedCloudUrl, apiToken, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return (null, ex.Message);
                }
            }
        }

        string? normalizedChromePath = null;
        if (request.LocalChromeEnabled)
        {
            if (!TryNormalizeLocalChromeExecutablePath(request.LocalChromeExecutablePath, out normalizedChromePath, out var chromePathError))
            {
                return (null, chromePathError);
            }
        }

        var maxResponseAgeDays = ResponseCollectionFilters.ClampResponseAgeDays(request.ResponseFilterMaxResponseAgeDays);
        var filters = ResponseCollectionFilters.NormalizeLegacy(
            request.ResponseFilterEnabled,
            request.ResponseFilterExcludeFemale,
            request.ResponseFilterMaxAge,
            request.ResponseFilterExcludeMale,
            request.ResponseFilterMaxAgeMale,
            request.ResponseFilterMaxAgeFemale,
            maxResponseAgeDays);
        var normalizedHighlightBuckets = ResponseHighlightRules.NormalizeBucketsCsv(request.ResponseHighlightAgeBuckets);
        var normalizedHighlightTargets = ResponseHighlightRules.NormalizeTargetsJson(request.ResponseHighlightTargetsJson);
        var highlightEnabled = request.ResponseHighlightEnabled
            && (!string.IsNullOrWhiteSpace(normalizedHighlightBuckets)
                || !string.IsNullOrWhiteSpace(normalizedHighlightTargets));
        var normalizedScheduleDays = WorkerScheduleRules.NormalizeDaysCsv(request.AutoScheduleDays);
        var normalizedScheduleFrom = WorkerScheduleRules.NormalizeTime(request.AutoScheduleFromLocalTime);
        var normalizedScheduleTo = WorkerScheduleRules.NormalizeTime(request.AutoScheduleToLocalTime);
        var autoScheduleEnabled = request.AutoScheduleEnabled
            && !string.IsNullOrWhiteSpace(normalizedScheduleDays)
            && normalizedScheduleFrom is not null
            && normalizedScheduleTo is not null
            && !string.Equals(normalizedScheduleFrom, normalizedScheduleTo, StringComparison.Ordinal);

        worker.MaxConcurrentAccounts = request.MaxConcurrentAccounts;
        worker.RuCaptchaApiKey = normalizedRuCaptchaKey;
        if (request.AdsPowerEnabled)
        {
            if (Changed(worker.AdsPowerApiBaseUrl, normalizedBaseUrl)
                || Changed(worker.AdsPowerApiKey, normalizedApiKey))
            {
                ClearStoredCheck(worker, WorkerBrowserProviderKinds.AdsPower);
            }

            worker.AdsPowerApiBaseUrl = normalizedBaseUrl;
            worker.AdsPowerApiKey = normalizedApiKey;
            var normalizedGroupId = AdsPowerGroupsJson.NormalizeGroupId(request.AdsPowerGroupId);
            worker.AdsPowerGroupId = normalizedGroupId;
            worker.AdsPowerGroupName = normalizedGroupId is null
                ? null
                : AdsPowerGroupsJson.ResolveGroupName(
                      normalizedGroupId,
                      AdsPowerGroupsJson.Parse(worker.AdsPowerGroupsJson))
                  ?? worker.AdsPowerGroupName;
        }

        if (request.MultiloginEnabled)
        {
            if (Changed(worker.MultiloginLauncherUrl, normalizedLauncherUrl)
                || Changed(worker.MultiloginCloudApiUrl, normalizedCloudUrl)
                || normalizedToken is not null)
            {
                ClearStoredCheck(worker, WorkerBrowserProviderKinds.Multilogin);
            }

            worker.MultiloginLauncherUrl = normalizedLauncherUrl;
            worker.MultiloginCloudApiUrl = normalizedCloudUrl;
            if (normalizedToken is not null)
            {
                worker.MultiloginAutomationToken = normalizedToken;
            }
        }

        if (request.LocalChromeEnabled)
        {
            if (Changed(worker.LocalChromeExecutablePath, normalizedChromePath))
            {
                ClearStoredCheck(worker, WorkerBrowserProviderKinds.Local);
            }

            worker.LocalChromeExecutablePath = normalizedChromePath;
        }

        worker.AdsPowerEnabled = request.AdsPowerEnabled;
        worker.MultiloginEnabled = request.MultiloginEnabled;
        worker.LocalChromeEnabled = request.LocalChromeEnabled;
        worker.ResponseFilterEnabled = filters.Enabled;
        worker.ResponseFilterExcludeFemale = filters.ExcludeFemale;
        worker.ResponseFilterExcludeMale = filters.ExcludeMale;
        worker.ResponseFilterMaxAgeMale = filters.MaxAgeMaleInclusive;
        worker.ResponseFilterMaxAgeFemale = filters.MaxAgeFemaleInclusive;
        worker.ResponseFilterMaxAgeDays = filters.EffectiveMaxResponseAgeDays;
        worker.ResponseHighlightEnabled = highlightEnabled;
        worker.ResponseHighlightAgeBuckets = string.IsNullOrWhiteSpace(normalizedHighlightBuckets) ? null : normalizedHighlightBuckets;
        worker.ResponseHighlightTargetsJson = normalizedHighlightTargets;
        worker.AutoScheduleEnabled = autoScheduleEnabled;
        worker.AutoScheduleDays = string.IsNullOrWhiteSpace(normalizedScheduleDays) ? null : normalizedScheduleDays;
        worker.AutoScheduleFromLocalTime = normalizedScheduleFrom;
        worker.AutoScheduleToLocalTime = normalizedScheduleTo;
        if (request.AutoDeliverToCrm is bool autoCrm)
        {
            worker.AutoDeliverToCrm = autoCrm;
        }

        if (request.AutoDeliverToBitrix is bool autoBitrix)
        {
            worker.AutoDeliverToBitrix = autoBitrix;
        }

        var autoReplyMessage = NormalizeAutoReplyMessage(request.MessengerAutoReplyMessage);
        worker.MessengerAutoReplyEnabled = request.MessengerAutoReplyEnabled && !string.IsNullOrWhiteSpace(autoReplyMessage);
        worker.MessengerAutoReplyMessage = autoReplyMessage;
        worker.PhoneUnchangedHours = ResponsePhoneWatchRules.ClampUnchangedHours(request.PhoneUnchangedHours);

        // Legacy field: keep only when both genders share the same limit (old workers / DTO).
        worker.ResponseFilterMaxAge =
            filters.MaxAgeMaleInclusive is int m
            && filters.MaxAgeFemaleInclusive is int f
            && m == f
                ? m
                : filters.MaxAgeMaleInclusive ?? filters.MaxAgeFemaleInclusive;
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

    private static bool TryNormalizeMultiloginUrl(
        string? value,
        string defaultUrl,
        bool persistDefaultWhenEmpty,
        string fieldName,
        out string? normalized,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = persistDefaultWhenEmpty ? defaultUrl : null;
            error = null;
            return true;
        }

        var trimmed = value.Trim().TrimEnd('/');
        if (trimmed.Length > MultiloginWorkerSettings.MaxUrlLength)
        {
            normalized = null;
            error = $"{fieldName} не должен превышать {MultiloginWorkerSettings.MaxUrlLength} символов.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            normalized = null;
            error = $"Укажите корректный {fieldName} (http или https).";
            return false;
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    private static bool TryNormalizeMultiloginAutomationToken(
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
        if (trimmed.Length > MultiloginWorkerSettings.MaxAutomationTokenLength)
        {
            normalized = null;
            error = "Automation token Multilogin слишком длинный.";
            return false;
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    private static string? NormalizeAutoReplyMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var trimmed = message.Trim();
        return trimmed.Length > 2000 ? trimmed[..2000] : trimmed;
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

        if (request.IsEnabledInPanel && !IsBrowserProviderEnabled(account, worker))
        {
            return (null, WorkerBrowserProviderMessages.DisabledHint);
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

        return (ToAccountConfigDto(account, worker.AdsPowerApiBaseUrl, worker.AdsPowerApiKey, includeCredentials: false), null);
    }

    public async Task<(WorkerAccountCredentialsDto? Credentials, string? Error)> UpdateAccountCredentialsAsync(
        Guid workerId,
        Guid accountId,
        UpdateWorkerAccountCredentialsRequest request,
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

        if (request.Clear)
        {
            account.AvitoLogin = null;
            account.AvitoPasswordProtected = null;
        }
        else
        {
            var login = string.IsNullOrWhiteSpace(request.Login) ? null : request.Login.Trim();
            if (login is { Length: > 256 })
            {
                return (null, "Логин Avito не должен превышать 256 символов.");
            }

            if (login is not null)
            {
                account.AvitoLogin = login;
            }

            if (!string.IsNullOrEmpty(request.Password))
            {
                if (request.Password.Length > 256)
                {
                    return (null, "Пароль Avito не должен превышать 256 символов.");
                }

                if (string.IsNullOrWhiteSpace(account.AvitoLogin) && login is null)
                {
                    return (null, "Укажите логин Avito вместе с паролем.");
                }

                account.AvitoPasswordProtected = avitoSecrets.Protect(request.Password);
            }

            if (string.IsNullOrWhiteSpace(account.AvitoLogin)
                || string.IsNullOrWhiteSpace(account.AvitoPasswordProtected))
            {
                // partial credentials are useless for worker auto-login
                if (!string.IsNullOrWhiteSpace(account.AvitoLogin)
                    && string.IsNullOrWhiteSpace(account.AvitoPasswordProtected))
                {
                    return (null, "Укажите пароль Avito (или очистите учётные данные).");
                }
            }
        }

        account.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Accounts, PanelChangeKind.Dashboard],
            worker.OfficeId,
            workerId);
        await workerPushNotifier.PushConfigChangedAsync(workerId, ct).ConfigureAwait(false);

        return (ToCredentialsDto(account), null);
    }

    private WorkerAccountConfigDto ToAccountConfigDto(
        WorkerAccountEntity account,
        string? adsPowerApiBaseUrl,
        string? adsPowerApiKey,
        bool includeCredentials)
    {
        string? avitoLogin = null;
        string? avitoPassword = null;
        if (includeCredentials
            && !string.IsNullOrWhiteSpace(account.AvitoLogin)
            && avitoSecrets.TryUnprotect(account.AvitoPasswordProtected, out var password)
            && !string.IsNullOrEmpty(password))
        {
            avitoLogin = account.AvitoLogin.Trim();
            avitoPassword = password;
        }

        return new(
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
            account.DraftsCount,
            avitoLogin,
            avitoPassword,
            ResolveProfileProvider(account),
            NullIfWhiteSpace(account.MultiloginProfileId),
            NullIfWhiteSpace(account.MultiloginFolderId),
            NullIfWhiteSpace(account.LocalUserDataDir));
    }

    private static bool TryNormalizeLocalChromeExecutablePath(
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
        if (trimmed.Length > 512)
        {
            normalized = null;
            error = "Путь к Chrome/Chromium не должен превышать 512 символов.";
            return false;
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    internal static bool IsLocalAccount(WorkerAccountEntity account) =>
        !string.IsNullOrWhiteSpace(account.LocalUserDataDir)
        && string.IsNullOrWhiteSpace(account.MultiloginProfileId)
        && string.IsNullOrWhiteSpace(account.AdsPowerProfileId);

    internal static string ResolveProfileProvider(WorkerAccountEntity account)
    {
        if (!string.IsNullOrWhiteSpace(account.MultiloginProfileId))
        {
            return "Multilogin";
        }

        if (IsLocalAccount(account) || !string.IsNullOrWhiteSpace(account.LocalUserDataDir))
        {
            return "Local";
        }

        return "AdsPower";
    }

    internal static bool IsBrowserProviderEnabled(WorkerAccountEntity account, WorkerEntity worker) =>
        ResolveProfileProvider(account) switch
        {
            "Multilogin" => worker.MultiloginEnabled,
            "Local" => worker.LocalChromeEnabled,
            _ => worker.AdsPowerEnabled
        };

    private static bool IsProviderToggleEnabled(WorkerEntity worker, string provider) =>
        provider switch
        {
            WorkerBrowserProviderKinds.Multilogin => worker.MultiloginEnabled,
            WorkerBrowserProviderKinds.Local => worker.LocalChromeEnabled,
            _ => worker.AdsPowerEnabled
        };

    private static bool Changed(string? left, string? right) =>
        !string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static void ClearStoredCheck(WorkerEntity worker, string provider)
    {
        var state = BrowserProviderChecksJson.Parse(worker.BrowserProviderChecksJson);
        BrowserProviderChecksJson.Clear(state, provider);
        worker.BrowserProviderChecksJson = BrowserProviderChecksJson.Serialize(state);
        if (string.Equals(worker.PendingBrowserProviderCheck, provider, StringComparison.OrdinalIgnoreCase))
        {
            worker.PendingBrowserProviderCheck = null;
            worker.PendingBrowserProviderCheckAtUtc = null;
        }
    }

    private static WorkerPendingBrowserProviderCheckDto? ToPendingCheck(WorkerEntity worker) =>
        string.IsNullOrWhiteSpace(worker.PendingBrowserProviderCheck)
            ? null
            : new WorkerPendingBrowserProviderCheckDto(
                worker.PendingBrowserProviderCheck,
                worker.PendingBrowserProviderCheckAtUtc ?? DateTime.UtcNow);

    private static WorkerPendingBrowserProviderSyncDto? ToPendingSync(WorkerEntity worker) =>
        string.IsNullOrWhiteSpace(worker.PendingBrowserProviderSync)
            ? null
            : new WorkerPendingBrowserProviderSyncDto(
                worker.PendingBrowserProviderSync,
                worker.PendingBrowserProviderSyncAtUtc ?? DateTime.UtcNow);

    internal static WorkerBrowserProviderCheckDto MapProviderCheck(WorkerEntity worker, string provider)
    {
        var state = BrowserProviderChecksJson.Parse(worker.BrowserProviderChecksJson);
        var enabled = IsProviderToggleEnabled(worker, provider);
        var needsSetup = provider == WorkerBrowserProviderKinds.Multilogin
            && string.IsNullOrWhiteSpace(worker.MultiloginAutomationToken);
        return BrowserProviderChecksJson.ToDto(
            provider,
            enabled,
            needsSetup,
            worker.PendingBrowserProviderCheck,
            worker.PendingBrowserProviderSync,
            BrowserProviderChecksJson.Get(state, provider));
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static WorkerAccountCredentialsDto ToCredentialsDto(WorkerAccountEntity account) =>
        new(
            account.AccountId,
            string.IsNullOrWhiteSpace(account.AvitoLogin) ? null : account.AvitoLogin.Trim(),
            !string.IsNullOrWhiteSpace(account.AvitoPasswordProtected));

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

    public async Task<(WorkerBrowserProviderCheckDto? Check, string? Error)> RequestProviderCheckAsync(
        Guid workerId,
        string? provider,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var kind = WorkerBrowserProviderKinds.Normalize(provider);
        if (kind is null)
        {
            return (null, WorkerBrowserProviderMessages.UnknownProvider);
        }

        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return (null, "Воркер не найден.");
        }

        var worker = await db.Workers.FirstOrDefaultAsync(x => x.Id == workerId, ct);
        if (worker is null)
        {
            return (null, "Воркер не найден.");
        }

        if (!IsProviderToggleEnabled(worker, kind))
        {
            return (null, WorkerBrowserProviderMessages.ProviderOff);
        }

        if (kind == WorkerBrowserProviderKinds.Multilogin
            && string.IsNullOrWhiteSpace(worker.MultiloginAutomationToken))
        {
            return (null, WorkerBrowserProviderMessages.NeedsToken);
        }

        if (!string.IsNullOrWhiteSpace(worker.PendingBrowserProviderCheck))
        {
            return (null, WorkerBrowserProviderMessages.CheckAlreadyQueued);
        }

        worker.PendingBrowserProviderCheck = kind;
        worker.PendingBrowserProviderCheckAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        panelRealtime.Notify([PanelChangeKind.Workers], worker.OfficeId, worker.Id);
        await workerPushNotifier.PushConfigChangedAsync(workerId, ct).ConfigureAwait(false);
        return (MapProviderCheck(worker, kind), null);
    }

    public async Task<(WorkerBrowserProviderCheckDto? Check, string? Error)> RequestProviderSyncAsync(
        Guid workerId,
        string? provider,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var kind = WorkerBrowserProviderKinds.Normalize(provider);
        if (kind is null || !WorkerBrowserProviderKinds.SupportsCatalogSync(kind))
        {
            return (null, WorkerBrowserProviderMessages.UnknownProvider);
        }

        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return (null, "Воркер не найден.");
        }

        var worker = await db.Workers.FirstOrDefaultAsync(x => x.Id == workerId, ct);
        if (worker is null)
        {
            return (null, "Воркер не найден.");
        }

        if (!IsProviderToggleEnabled(worker, kind))
        {
            return (null, WorkerBrowserProviderMessages.ProviderOff);
        }

        if (kind == WorkerBrowserProviderKinds.Multilogin
            && string.IsNullOrWhiteSpace(worker.MultiloginAutomationToken))
        {
            return (null, WorkerBrowserProviderMessages.NeedsToken);
        }

        if (!string.IsNullOrWhiteSpace(worker.PendingBrowserProviderSync))
        {
            return (null, WorkerBrowserProviderMessages.SyncAlreadyQueued);
        }

        worker.PendingBrowserProviderSync = kind;
        worker.PendingBrowserProviderSyncAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        panelRealtime.Notify([PanelChangeKind.Workers, PanelChangeKind.Accounts], worker.OfficeId, worker.Id);
        await workerPushNotifier.PushConfigChangedAsync(workerId, ct).ConfigureAwait(false);
        return (MapProviderCheck(worker, kind), null);
    }

    public async Task<(bool Success, string? Error)> ReportProviderCheckAsync(
        Guid workerId,
        ReportWorkerBrowserProviderCheckRequest request,
        CancellationToken ct = default)
    {
        var kind = WorkerBrowserProviderKinds.Normalize(request.Provider);
        if (kind is null)
        {
            return (false, WorkerBrowserProviderMessages.UnknownProvider);
        }

        var worker = await db.Workers.FirstOrDefaultAsync(x => x.Id == workerId, ct);
        if (worker is null)
        {
            return (false, "Воркер не найден.");
        }

        var state = BrowserProviderChecksJson.Parse(worker.BrowserProviderChecksJson);
        var secrets = kind switch
        {
            WorkerBrowserProviderKinds.AdsPower => new[] { worker.AdsPowerApiKey },
            WorkerBrowserProviderKinds.Multilogin => new[] { worker.MultiloginAutomationToken },
            _ => Array.Empty<string?>()
        };
        BrowserProviderChecksJson.Set(
            state,
            kind,
            BrowserProviderChecksJson.FromReport(request, DateTime.UtcNow, secrets));
        worker.BrowserProviderChecksJson = BrowserProviderChecksJson.Serialize(state);

        if (kind == WorkerBrowserProviderKinds.AdsPower && request.Groups is not null)
        {
            var groups = AdsPowerGroupsJson.Parse(AdsPowerGroupsJson.Serialize(request.Groups));
            worker.AdsPowerGroupsJson = AdsPowerGroupsJson.Serialize(groups);
            if (!string.IsNullOrWhiteSpace(worker.AdsPowerGroupId))
            {
                worker.AdsPowerGroupName =
                    AdsPowerGroupsJson.ResolveGroupName(worker.AdsPowerGroupId, groups)
                    ?? worker.AdsPowerGroupName;
            }
        }

        if (string.Equals(worker.PendingBrowserProviderCheck, kind, StringComparison.OrdinalIgnoreCase))
        {
            worker.PendingBrowserProviderCheck = null;
            worker.PendingBrowserProviderCheckAtUtc = null;
        }

        if (request.CompletesSync
            && string.Equals(worker.PendingBrowserProviderSync, kind, StringComparison.OrdinalIgnoreCase))
        {
            worker.PendingBrowserProviderSync = null;
            worker.PendingBrowserProviderSyncAtUtc = null;
        }

        await db.SaveChangesAsync(ct);
        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Accounts],
            worker.OfficeId,
            worker.Id);
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

        if (string.IsNullOrWhiteSpace(account.AdsPowerProfileId)
            && string.IsNullOrWhiteSpace(account.MultiloginProfileId)
            && string.IsNullOrWhiteSpace(account.LocalUserDataDir))
        {
            return (false, "Аккаунт не привязан к браузерному профилю.");
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

    public async Task<(WorkerAccountConfigDto? Account, string? Error)> CreateLocalAccountAsync(
        Guid workerId,
        CreateLocalWorkerAccountRequest request,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (!TryNormalizeDisplayName(request.DisplayName, out var displayName, out var nameError))
        {
            return (null, nameError);
        }

        string? userDataDir = null;
        var attachExisting = !string.IsNullOrWhiteSpace(request.LocalUserDataDir);
        if (attachExisting
            && !TryNormalizeLocalUserDataDir(request.LocalUserDataDir, out userDataDir, out var pathError))
        {
            return (null, pathError);
        }

        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return (null, "Воркер не найден.");
        }

        var worker = await db.Workers.FirstOrDefaultAsync(x => x.Id == workerId, ct);
        if (worker is null)
        {
            return (null, "Воркер не найден.");
        }

        if (attachExisting)
        {
            var duplicate = await db.WorkerAccounts.AnyAsync(
                x => x.WorkerId == workerId && x.LocalUserDataDir == userDataDir,
                ct);
            if (duplicate)
            {
                return (null, "Аккаунт с этой папкой профиля уже добавлен.");
            }
        }

        var now = DateTime.UtcNow;
        var accountId = Guid.NewGuid();
        var created = new WorkerAccountEntity
        {
            WorkerId = workerId,
            AccountId = accountId,
            AdsPowerProfileId = string.Empty,
            LocalUserDataDir = attachExisting
                ? userDataDir
                : LocalChromeProfileMarkers.CreateManaged(accountId),
            DisplayName = displayName,
            Status = attachExisting ? string.Empty : "RequiresLogin",
            IsEnabled = false,
            IsEnabledInPanel = false,
            UpdatedAtUtc = now
        };
        db.WorkerAccounts.Add(created);
        await db.SaveChangesAsync(ct);

        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Accounts, PanelChangeKind.Dashboard],
            worker.OfficeId,
            workerId);
        await workerPushNotifier.PushConfigChangedAsync(workerId, ct).ConfigureAwait(false);

        return (ToAccountConfigDto(created, worker.AdsPowerApiBaseUrl, worker.AdsPowerApiKey, includeCredentials: false), null);
    }

    public async Task<(WorkerAccountConfigDto? Account, string? Error)> UpdateLocalAccountAsync(
        Guid workerId,
        Guid accountId,
        UpdateLocalWorkerAccountRequest request,
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

        if (!IsLocalAccount(account))
        {
            return (null, "Редактирование папки профиля доступно только для обычного браузера.");
        }

        if (request.DisplayName is not null)
        {
            if (!TryNormalizeDisplayName(request.DisplayName, out var displayName, out var nameError))
            {
                return (null, nameError);
            }

            account.DisplayName = displayName;
        }

        if (request.LocalUserDataDir is not null)
        {
            if (!TryNormalizeLocalUserDataDir(request.LocalUserDataDir, out var userDataDir, out var pathError))
            {
                return (null, pathError);
            }

            var duplicate = await db.WorkerAccounts.AnyAsync(
                x => x.WorkerId == workerId
                    && x.AccountId != accountId
                    && x.LocalUserDataDir == userDataDir,
                ct);
            if (duplicate)
            {
                return (null, "Аккаунт с этой папкой профиля уже добавлен.");
            }

            account.LocalUserDataDir = userDataDir;
        }

        account.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Accounts, PanelChangeKind.Dashboard],
            worker.OfficeId,
            workerId);
        await workerPushNotifier.PushConfigChangedAsync(workerId, ct).ConfigureAwait(false);

        return (ToAccountConfigDto(account, worker.AdsPowerApiBaseUrl, worker.AdsPowerApiKey, includeCredentials: false), null);
    }

    public async Task<(bool Success, string? Error)> DeleteLocalAccountAsync(
        Guid workerId,
        Guid accountId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return (false, "Воркер не найден.");
        }

        var worker = await db.Workers.AsNoTracking()
            .Where(x => x.Id == workerId)
            .Select(x => new { x.OfficeId })
            .FirstOrDefaultAsync(ct);
        if (worker is null)
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

        if (!IsLocalAccount(account))
        {
            return (false, "Удаление доступно только для аккаунтов обычного браузера. Папка профиля на диске не удаляется.");
        }

        db.WorkerAccounts.Remove(account);
        await db.SaveChangesAsync(ct);

        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Accounts, PanelChangeKind.Dashboard],
            worker.OfficeId,
            workerId);
        await workerPushNotifier.PushConfigChangedAsync(workerId, ct).ConfigureAwait(false);

        return (true, null);
    }

    private static bool TryNormalizeDisplayName(string? value, out string normalized, out string? error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Укажите имя аккаунта.";
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 200)
        {
            error = "Имя аккаунта не должно превышать 200 символов.";
            return false;
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    private static bool TryNormalizeLocalUserDataDir(string? value, out string normalized, out string? error) =>
        LocalChromeUserDataRules.TryNormalizeAttachedDir(value, out normalized, out error);
}

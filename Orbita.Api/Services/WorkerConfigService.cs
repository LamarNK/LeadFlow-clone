using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerConfigService(OrbitaDbContext db, OfficeScopeService officeScope)
{
    public async Task<WorkerConfigDto?> GetConfigForWorkerAsync(
        Guid workerId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return null;
        }

        var worker = await db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == workerId, ct);
        if (worker is null)
        {
            return null;
        }

        var accounts = await db.WorkerAccounts
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId)
            .OrderBy(x => x.DisplayName)
            .Select(x => new WorkerAccountConfigDto(
                x.AccountId,
                x.AdsPowerProfileId,
                x.DisplayName,
                x.IsEnabledInPanel,
                worker.AdsPowerApiBaseUrl,
                worker.AdsPowerApiKey))
            .ToListAsync(ct);

        return new WorkerConfigDto(
            worker.Id,
            worker.MaxConcurrentAccounts,
            worker.AdsPowerApiBaseUrl,
            worker.AdsPowerApiKey,
            accounts);
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

        worker.MaxConcurrentAccounts = request.MaxConcurrentAccounts;
        worker.AdsPowerApiBaseUrl = normalizedBaseUrl;
        worker.AdsPowerApiKey = normalizedApiKey;
        await db.SaveChangesAsync(ct);
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
        account.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return (new WorkerAccountConfigDto(
            account.AccountId,
            account.AdsPowerProfileId,
            account.DisplayName,
            account.IsEnabledInPanel,
            worker.AdsPowerApiBaseUrl,
            worker.AdsPowerApiKey), null);
    }
}
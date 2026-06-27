using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerAdminService(
    OrbitaDbContext db,
    IConfiguration configuration,
    WorkerReleaseService releases)
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

        var resolvedOfficeId = await ResolveOfficeIdForCreateAsync(officeId, scope, ct);
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
            .Where(x => scope.IsGlobalAdmin
                ? officeFilter is null || x.OfficeId == officeFilter
                : x.OfficeId == scope.OfficeId)
            .Select(x => Map(x, now, latestReleaseVersion))
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
        if (worker is null || !scope.CanAccessOffice(worker.OfficeId))
        {
            return (null, "Воркер не найден.");
        }

        worker.DisplayName = displayName.Trim();
        await db.SaveChangesAsync(ct);
        return (Map(worker), null);
    }

    public async Task<(AdminWorkerListItemDto? Worker, string? Error)> SetEnabledAsync(
        Guid id,
        bool enabled,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var worker = await db.Workers.Include(x => x.Office).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (worker is null || !scope.CanAccessOffice(worker.OfficeId))
        {
            return (null, "Воркер не найден.");
        }

        worker.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        return (Map(worker), null);
    }

    public async Task<(RotateWorkerApiKeyResponse? Result, string? Error)> RotateApiKeyAsync(
        Guid id,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var worker = await db.Workers.FindAsync([id], ct);
        if (worker is null || !scope.CanAccessOffice(worker.OfficeId))
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

    private async Task<Guid?> ResolveOfficeIdForCreateAsync(Guid? officeId, OfficeScope scope, CancellationToken ct)
    {
        if (scope.IsGlobalAdmin)
        {
            return officeId;
        }

        return scope.OfficeId;
    }

    private static AdminWorkerListItemDto Map(
        WorkerEntity worker,
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
            WorkerOnlineRules.IsOnline(worker.LastSeenAtUtc, now),
            worker.LastSeenAtUtc,
            worker.CreatedAtUtc,
            worker.ApiKeyRotatedAtUtc,
            updateAvailable,
            latestReleaseVersion,
            worker.OfficeId,
            worker.Office?.Name ?? string.Empty);
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
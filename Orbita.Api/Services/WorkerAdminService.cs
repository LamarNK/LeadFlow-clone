using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerAdminService(OrbitaDbContext db, IConfiguration configuration)
{
    public async Task<IReadOnlyList<AdminWorkerListItemDto>> ListAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await db.Workers
            .AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .Select(x => new AdminWorkerListItemDto(
                x.Id,
                x.DisplayName,
                x.MachineName,
                x.AppVersion,
                x.IsEnabled,
                x.LastSeenAtUtc.HasValue && now - x.LastSeenAtUtc.Value <= WorkerOnlineRules.OnlineThreshold,
                x.LastSeenAtUtc,
                x.CreatedAtUtc,
                x.ApiKeyRotatedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<(AdminWorkerListItemDto? Worker, string? Error)> RenameAsync(
        Guid id,
        string displayName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (null, "Имя воркера обязательно.");
        }

        var worker = await db.Workers.FindAsync([id], ct);
        if (worker is null)
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
        CancellationToken ct = default)
    {
        var worker = await db.Workers.FindAsync([id], ct);
        if (worker is null)
        {
            return (null, "Воркер не найден.");
        }

        worker.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        return (Map(worker), null);
    }

    public async Task<(RotateWorkerApiKeyResponse? Result, string? Error)> RotateApiKeyAsync(
        Guid id,
        CancellationToken ct = default)
    {
        var worker = await db.Workers.FindAsync([id], ct);
        if (worker is null)
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
            "config");
    }

    private static AdminWorkerListItemDto Map(WorkerEntity worker)
    {
        var now = DateTime.UtcNow;
        return new AdminWorkerListItemDto(
            worker.Id,
            worker.DisplayName,
            worker.MachineName,
            worker.AppVersion,
            worker.IsEnabled,
            WorkerOnlineRules.IsOnline(worker.LastSeenAtUtc, now),
            worker.LastSeenAtUtc,
            worker.CreatedAtUtc,
            worker.ApiKeyRotatedAtUtc);
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
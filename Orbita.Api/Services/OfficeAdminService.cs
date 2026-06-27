using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class OfficeAdminService(OrbitaDbContext db)
{
    public async Task<IReadOnlyList<OfficeDto>> ListAsync(CancellationToken ct = default)
    {
        var workerCounts = await db.Workers
            .AsNoTracking()
            .GroupBy(x => x.OfficeId)
            .Select(g => new { OfficeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OfficeId, x => x.Count, ct);

        var userCounts = await db.PanelUserProfiles
            .AsNoTracking()
            .Where(x => x.OfficeId != null)
            .GroupBy(x => x.OfficeId!.Value)
            .Select(g => new { OfficeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OfficeId, x => x.Count, ct);

        var offices = await db.Offices.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        return offices
            .Select(x => new OfficeDto(
                x.Id,
                x.Name,
                x.IsEnabled,
                x.CreatedAtUtc,
                workerCounts.GetValueOrDefault(x.Id),
                userCounts.GetValueOrDefault(x.Id)))
            .ToList();
    }

    public async Task<OfficeDetailDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var office = await db.Offices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return office is null ? null : MapDetail(office);
    }

    public async Task<(OfficeDetailDto? Office, string? Error)> CreateAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, "Название офиса обязательно.");
        }

        var trimmedName = name.Trim();
        if (await db.Offices.AnyAsync(x => x.Name == trimmedName, ct))
        {
            return (null, "Офис с таким названием уже существует.");
        }

        var secret = ApiKeyService.GenerateApiKey();
        var office = new OfficeEntity
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            RegistrationSecretHash = ApiKeyService.HashApiKey(secret),
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true
        };

        db.Offices.Add(office);
        await db.SaveChangesAsync(ct);
        return (MapDetail(office), null);
    }

    public async Task<(OfficeDetailDto? Office, string? Error)> UpdateAsync(
        Guid id,
        string name,
        bool isEnabled,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, "Название офиса обязательно.");
        }

        var office = await db.Offices.FindAsync([id], ct);
        if (office is null)
        {
            return (null, "Офис не найден.");
        }

        var trimmedName = name.Trim();
        if (await db.Offices.AnyAsync(x => x.Id != id && x.Name == trimmedName, ct))
        {
            return (null, "Офис с таким названием уже существует.");
        }

        office.Name = trimmedName;
        office.IsEnabled = isEnabled;
        await db.SaveChangesAsync(ct);
        return (MapDetail(office), null);
    }

    public async Task<(RotateOfficeRegistrationSecretResponse? Result, string? Error)> RotateRegistrationSecretAsync(
        Guid id,
        CancellationToken ct = default)
    {
        var office = await db.Offices.FindAsync([id], ct);
        if (office is null)
        {
            return (null, "Офис не найден.");
        }

        var secret = ApiKeyService.GenerateApiKey();
        office.RegistrationSecretHash = ApiKeyService.HashApiKey(secret);
        await db.SaveChangesAsync(ct);
        return (new RotateOfficeRegistrationSecretResponse(office.Id, secret), null);
    }

    public async Task<OfficeRegistrationInfoDto?> GetRegistrationInfoAsync(Guid id, CancellationToken ct = default)
    {
        var office = await db.Offices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (office is null)
        {
            return null;
        }

        return new OfficeRegistrationInfoDto(
            office.Id,
            office.Name,
            !string.IsNullOrWhiteSpace(office.RegistrationSecretHash),
            MaskSecret(office.RegistrationSecretHash));
    }

    public async Task<OfficeEntity?> FindByRegistrationSecretAsync(string registrationSecret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(registrationSecret))
        {
            return null;
        }

        var hash = ApiKeyService.HashApiKey(registrationSecret);
        return await db.Offices
            .FirstOrDefaultAsync(x => x.RegistrationSecretHash == hash && x.IsEnabled, ct);
    }

    public async Task<OfficeEntity> EnsureDefaultOfficeAsync(string? registrationSecret, CancellationToken ct = default)
    {
        var existing = await db.Offices.OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(registrationSecret)
                && string.IsNullOrWhiteSpace(existing.RegistrationSecretHash))
            {
                existing.RegistrationSecretHash = ApiKeyService.HashApiKey(registrationSecret);
                await db.SaveChangesAsync(ct);
            }

            return existing;
        }

        var secret = string.IsNullOrWhiteSpace(registrationSecret)
            ? ApiKeyService.GenerateApiKey()
            : registrationSecret.Trim();

        var office = new OfficeEntity
        {
            Id = Guid.NewGuid(),
            Name = "Основной",
            RegistrationSecretHash = ApiKeyService.HashApiKey(secret),
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true
        };

        db.Offices.Add(office);
        await db.SaveChangesAsync(ct);
        return office;
    }

    private static OfficeDetailDto MapDetail(OfficeEntity office) =>
        new(
            office.Id,
            office.Name,
            office.IsEnabled,
            office.CreatedAtUtc,
            !string.IsNullOrWhiteSpace(office.RegistrationSecretHash),
            MaskSecret(office.RegistrationSecretHash));

    private static string MaskSecret(string hashOrSecret)
    {
        if (string.IsNullOrWhiteSpace(hashOrSecret))
        {
            return "—";
        }

        return hashOrSecret.Length <= 4 ? "****" : $"****{hashOrSecret[^4..]}";
    }
}
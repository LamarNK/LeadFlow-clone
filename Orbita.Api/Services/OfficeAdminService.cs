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
                userCounts.GetValueOrDefault(x.Id),
                x.BitrixValidationStatus,
                x.BitrixPortalHost))
            .ToList();
    }

    /// <summary>Enabled offices for pickers (workers, CRM delivery targets).</summary>
    public async Task<IReadOnlyList<OfficeOptionDto>> ListOptionsAsync(CancellationToken ct = default) =>
        await db.Offices.AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .Select(x => new OfficeOptionDto(x.Id, x.Name, x.IsEnabled, x.CrmEnabled))
            .ToListAsync(ct);

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
        bool bitrixTransmissionEnabled,
        bool crmEnabled,
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
        office.BitrixTransmissionEnabled = bitrixTransmissionEnabled;
        office.CrmEnabled = crmEnabled;

        var route = await db.DistributionRoutes.FirstOrDefaultAsync(x => x.OfficeId == office.Id, ct);
        var now = DateTime.UtcNow;
        if (route is null)
        {
            db.DistributionRoutes.Add(new DistributionRouteEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = office.Id,
                IsAutoDistributionEnabled = bitrixTransmissionEnabled,
                UpdatedAtUtc = now
            });
        }
        else
        {
            route.IsAutoDistributionEnabled = bitrixTransmissionEnabled;
            route.UpdatedAtUtc = now;
        }

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

    /// <summary>
    /// Deletes an office. Blocked when it is the last office or still has workers.
    /// Operators are unlinked; office-owned CRM/Bitrix data is cleaned up first
    /// because several FKs use Restrict rather than cascade.
    /// </summary>
    public async Task<(bool Success, string? Error, string? OfficeName)> DeleteAsync(
        Guid id,
        CancellationToken ct = default)
    {
        var office = await db.Offices.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (office is null)
        {
            return (false, "Офис не найден.", null);
        }

        if (await db.Offices.CountAsync(ct) <= 1)
        {
            return (false, "Нельзя удалить последний офис.", null);
        }

        var workerCount = await db.Workers.CountAsync(x => x.OfficeId == id, ct);
        if (workerCount > 0)
        {
            return (
                false,
                $"Нельзя удалить офис: к нему привязано воркеров — {workerCount}. Сначала удалите или перенесите воркеры.",
                null);
        }

        var profiles = await db.PanelUserProfiles.Where(x => x.OfficeId == id).ToListAsync(ct);
        foreach (var profile in profiles)
        {
            profile.OfficeId = null;
        }

        await CleanupOfficeOwnedDataAsync(id, ct);

        db.Offices.Remove(office);
        await db.SaveChangesAsync(ct);
        return (true, null, office.Name);
    }

    private async Task CleanupOfficeOwnedDataAsync(Guid officeId, CancellationToken ct)
    {
        // Prefer tracked RemoveRange over ExecuteDelete so unit tests (InMemory) work
        // and Restrict FKs are cleared before Office removal.

        var crmDeliveries = await db.ResponseCrmDeliveries
            .Where(x => x.OfficeId == officeId)
            .ToListAsync(ct);
        db.ResponseCrmDeliveries.RemoveRange(crmDeliveries);

        var bitrixIds = await db.BitrixInstances
            .Where(x => x.OfficeId == officeId)
            .Select(x => x.Id)
            .ToListAsync(ct);

        if (bitrixIds.Count > 0)
        {
            var bitrixDeliveries = await db.ResponseBitrixDeliveries
                .Where(x => bitrixIds.Contains(x.BitrixInstanceId))
                .ToListAsync(ct);
            db.ResponseBitrixDeliveries.RemoveRange(bitrixDeliveries);

            var workforceAssignments = await db.BitrixWorkforceAssignments
                .Where(x => bitrixIds.Contains(x.BitrixInstanceId))
                .ToListAsync(ct);
            db.BitrixWorkforceAssignments.RemoveRange(workforceAssignments);
        }

        // Distribution nodes Restrict→BitrixInstance: drop routes (and nodes) before Bitrix cascade.
        var routes = await db.DistributionRoutes
            .Where(x => x.OfficeId == officeId)
            .ToListAsync(ct);
        if (routes.Count > 0)
        {
            var routeIds = routes.Select(x => x.Id).ToList();
            var roundRobin = await db.DistributionRoundRobinStates
                .Where(x => routeIds.Contains(x.RouteId))
                .ToListAsync(ct);
            db.DistributionRoundRobinStates.RemoveRange(roundRobin);

            var nodes = await db.DistributionNodes
                .Where(x => routeIds.Contains(x.RouteId))
                .ToListAsync(ct);
            // Children first (self-FK ParentNodeId).
            db.DistributionNodes.RemoveRange(nodes.Where(x => x.ParentNodeId != null));
            db.DistributionNodes.RemoveRange(nodes.Where(x => x.ParentNodeId is null));
            db.DistributionRoutes.RemoveRange(routes);
        }

        // CRM cards/tasks have OfficeId without cascade FKs — remove explicitly.
        var cards = await db.CrmCandidateCards
            .Where(x => x.OfficeId == officeId)
            .ToListAsync(ct);
        var cardIds = cards.Select(x => x.Id).ToList();

        if (cardIds.Count > 0)
        {
            var notes = await db.CrmCandidateNotes
                .Where(x => cardIds.Contains(x.CardId))
                .ToListAsync(ct);
            db.CrmCandidateNotes.RemoveRange(notes);

            var history = await db.CrmCandidateHistory
                .Where(x => cardIds.Contains(x.CardId))
                .ToListAsync(ct);
            db.CrmCandidateHistory.RemoveRange(history);
        }

        var tasks = await db.CrmTasks
            .Where(x => x.OfficeId == officeId
                || (x.CardId != null && cardIds.Contains(x.CardId.Value)))
            .ToListAsync(ct);
        var taskIds = tasks.Select(x => x.Id).ToList();

        var notifications = await db.CrmTaskNotifications
            .Where(x => x.OfficeId == officeId || taskIds.Contains(x.TaskId))
            .ToListAsync(ct);
        db.CrmTaskNotifications.RemoveRange(notifications);

        if (taskIds.Count > 0)
        {
            var comments = await db.CrmTaskComments
                .Where(x => taskIds.Contains(x.TaskId))
                .ToListAsync(ct);
            db.CrmTaskComments.RemoveRange(comments);

            var attachments = await db.CrmTaskAttachments
                .Where(x => taskIds.Contains(x.TaskId))
                .ToListAsync(ct);
            db.CrmTaskAttachments.RemoveRange(attachments);

            db.CrmTasks.RemoveRange(tasks);
        }

        if (cards.Count > 0)
        {
            db.CrmCandidateCards.RemoveRange(cards);
        }

        var shifts = await db.CrmManagerShifts
            .Where(x => x.OfficeId == officeId)
            .ToListAsync(ct);
        db.CrmManagerShifts.RemoveRange(shifts);

        var captchas = await db.CaptchaSessions
            .Where(x => x.OfficeId == officeId)
            .ToListAsync(ct);
        db.CaptchaSessions.RemoveRange(captchas);

        if (bitrixIds.Count > 0)
        {
            // Remove Bitrix rows here so Restrict paths are already clear before Office delete.
            var bitrixInstances = await db.BitrixInstances
                .Where(x => bitrixIds.Contains(x.Id))
                .ToListAsync(ct);
            db.BitrixInstances.RemoveRange(bitrixInstances);
        }
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

    private static OfficeDetailDto MapDetail(OfficeEntity office)
    {
        var bitrix = OfficeBitrixIntegrationService.MapDto(office);
        return new(
            office.Id,
            office.Name,
            office.IsEnabled,
            office.BitrixTransmissionEnabled,
            office.CreatedAtUtc,
            !string.IsNullOrWhiteSpace(office.RegistrationSecretHash),
            MaskSecret(office.RegistrationSecretHash),
            bitrix.ValidationStatus,
            bitrix.ValidationMessage,
            bitrix.MaskedWebhookUrl,
            bitrix.PortalHost,
            bitrix.LastValidatedAtUtc,
            office.CrmEnabled);
    }

    private static string MaskSecret(string hashOrSecret)
    {
        if (string.IsNullOrWhiteSpace(hashOrSecret))
        {
            return "—";
        }

        return hashOrSecret.Length <= 4 ? "****" : $"****{hashOrSecret[^4..]}";
    }
}
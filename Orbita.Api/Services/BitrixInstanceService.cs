using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class BitrixInstanceService(
    OrbitaDbContext db,
    WebhookSecretProtector protector,
    BitrixWebhookValidator validator,
    IOptions<OrbitaBitrixSettings> defaultBitrixOptions,
    PanelAuditService audit)
{
    public async Task<IReadOnlyList<BitrixInstanceListItemDto>> ListAsync(
        OfficeScope scope,
        Guid? officeId,
        CancellationToken ct = default)
    {
        var (targetOfficeId, _) = OfficeIdResolver.Resolve(scope, officeId);
        if (targetOfficeId is not Guid resolvedOfficeId)
        {
            return [];
        }

        return await db.BitrixInstances
            .AsNoTracking()
            .Where(x => x.OfficeId == resolvedOfficeId)
            .OrderBy(x => x.Name)
            .Select(x => new BitrixInstanceListItemDto(
                x.Id,
                x.Name,
                x.Signature,
                x.PortalHost,
                x.ValidationStatus,
                x.ValidationMessage,
                x.IsEnabled,
                x.LeadExportLimit,
                x.LeadExportSessionCount))
            .ToListAsync(ct);
    }

    public async Task<BitrixInstanceDto?> GetAsync(
        Guid id,
        OfficeScope scope,
        Guid? officeId,
        CancellationToken ct = default)
    {
        var entity = await db.BitrixInstances.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null || !CanAccessOffice(scope, officeId, entity.OfficeId))
        {
            return null;
        }

        return MapDto(entity);
    }

    public async Task<(BitrixInstanceDto? Instance, string? Error)> CreateAsync(
        OfficeScope scope,
        Guid? officeId,
        CreateBitrixInstanceRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        var (targetOfficeId, error) = OfficeIdResolver.Resolve(scope, officeId);
        if (targetOfficeId is not Guid resolvedOfficeId)
        {
            return (null, error);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (null, "Укажите название Битрикса.");
        }

        var normalized = BitrixWebhookValidator.NormalizeWebhookUrl(request.WebhookUrl);
        if (normalized is null)
        {
            var formatIssue = BitrixWebhookValidator.GetFormatIssue(request.WebhookUrl);
            return (null, formatIssue?.Message ?? "Укажите корректную ссылку входящего вебхука Bitrix24.");
        }

        var validation = await validator.ValidateAsync(normalized, ct);
        var integration = ResolveIntegrationSettings();
        var now = DateTime.UtcNow;
        var entity = new BitrixInstanceEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = resolvedOfficeId,
            Name = request.Name.Trim(),
            Signature = request.Signature?.Trim() ?? string.Empty,
            WebhookUrlProtected = protector.Protect(normalized),
            PortalHost = BitrixWebhookValidator.TryGetPortalHost(normalized),
            ValidationStatus = validation.Status,
            ValidationMessage = validation.Message,
            LastValidatedAtUtc = now,
            IntegrationSettingsJson = integration.Serialize(),
            IsEnabled = request.IsEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            UpdatedByUserId = actorUserId
        };

        db.BitrixInstances.Add(entity);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            actorUserId,
            null,
            PanelAuditOfficeActions.BitrixInstanceCreated,
            "bitrix_instance",
            entity.Id.ToString(),
            entity.Name,
            null,
            ct);

        return (MapDto(entity), null);
    }

    public async Task<(BitrixInstanceDto? Instance, string? Error)> UpdateAsync(
        Guid id,
        OfficeScope scope,
        Guid? officeId,
        UpdateBitrixInstanceRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        var entity = await db.BitrixInstances.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null || !CanAccessOffice(scope, officeId, entity.OfficeId))
        {
            return (null, "Битрикс не найден.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (null, "Укажите название Битрикса.");
        }

        entity.Name = request.Name.Trim();
        entity.Signature = request.Signature?.Trim() ?? string.Empty;
        entity.IsEnabled = request.IsEnabled;
        entity.IntegrationSettingsJson = ResolveIntegrationSettings().Serialize();

        if (!string.IsNullOrWhiteSpace(request.WebhookUrl))
        {
            var normalized = BitrixWebhookValidator.NormalizeWebhookUrl(request.WebhookUrl);
            if (normalized is null)
            {
                return (null, "Укажите корректную ссылку входящего вебхука Bitrix24.");
            }

            var validation = await validator.ValidateAsync(normalized, ct);
            entity.WebhookUrlProtected = protector.Protect(normalized);
            entity.PortalHost = BitrixWebhookValidator.TryGetPortalHost(normalized);
            entity.ValidationStatus = validation.Status;
            entity.ValidationMessage = validation.Message;
            entity.LastValidatedAtUtc = DateTime.UtcNow;
        }

        entity.UpdatedAtUtc = DateTime.UtcNow;
        entity.UpdatedByUserId = actorUserId;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            actorUserId,
            null,
            PanelAuditOfficeActions.BitrixInstanceUpdated,
            "bitrix_instance",
            entity.Id.ToString(),
            entity.Name,
            null,
            ct);

        return (MapDto(entity), null);
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(
        Guid id,
        OfficeScope scope,
        Guid? officeId,
        CancellationToken ct = default)
    {
        var entity = await db.BitrixInstances.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null || !CanAccessOffice(scope, officeId, entity.OfficeId))
        {
            return (false, "Битрикс не найден.");
        }

        var usedInRoute = await db.DistributionNodes.AnyAsync(x => x.BitrixInstanceId == id, ct);
        if (usedInRoute)
        {
            return (false, "Битрикс используется в схеме связей. Сначала удалите его из редактора связей.");
        }

        var name = entity.Name;
        db.BitrixInstances.Remove(entity);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            null,
            null,
            PanelAuditOfficeActions.BitrixInstanceDeleted,
            "bitrix_instance",
            id.ToString(),
            name,
            null,
            ct);

        return (true, null);
    }

    public async Task<(BitrixWebhookValidationDto? Validation, string? Error)> ValidateAsync(
        Guid id,
        OfficeScope scope,
        Guid? officeId,
        string? webhookUrl,
        string actorUserId,
        bool persistResult,
        CancellationToken ct = default)
    {
        var entity = await db.BitrixInstances.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null || !CanAccessOffice(scope, officeId, entity.OfficeId))
        {
            return (null, "Битрикс не найден.");
        }

        var resolvedUrl = await ResolveWebhookUrlAsync(entity, webhookUrl, ct);
        if (resolvedUrl is null)
        {
            return (null, "Укажите URL вебхука для проверки.");
        }

        var validation = await validator.ValidateAsync(resolvedUrl, ct);
        if (persistResult)
        {
            entity.ValidationStatus = validation.Status;
            entity.ValidationMessage = validation.Message;
            entity.LastValidatedAtUtc = DateTime.UtcNow;
            entity.UpdatedAtUtc = DateTime.UtcNow;
            entity.UpdatedByUserId = actorUserId;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                actorUserId,
                null,
                PanelAuditOfficeActions.BitrixInstanceValidated,
                "bitrix_instance",
                entity.Id.ToString(),
                validation.Status,
                null,
                ct);
        }

        return (validation, null);
    }

    public async Task<string?> ResolveWebhookUrlAsync(BitrixInstanceEntity instance, CancellationToken ct = default) =>
        await ResolveWebhookUrlAsync(instance, null, ct);

    public async Task<IReadOnlyList<BitrixInstanceEntity>> GetEnabledForOfficeAsync(Guid officeId, CancellationToken ct = default) =>
        await db.BitrixInstances
            .AsNoTracking()
            .Where(x => x.OfficeId == officeId && x.IsEnabled)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

    private async Task<string?> ResolveWebhookUrlAsync(
        BitrixInstanceEntity instance,
        string? webhookUrl,
        CancellationToken ct)
    {
        var normalized = BitrixWebhookValidator.NormalizeWebhookUrl(webhookUrl);
        if (normalized is not null)
        {
            return normalized;
        }

        if (string.IsNullOrWhiteSpace(instance.WebhookUrlProtected))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(instance.WebhookUrlProtected);
        }
        catch
        {
            return null;
        }
    }

    private BitrixInstanceDto MapDto(BitrixInstanceEntity entity)
    {
        var integration = BitrixInstanceIntegrationSettings.Parse(
            entity.IntegrationSettingsJson,
            defaultBitrixOptions.Value);
        return new BitrixInstanceDto(
            entity.Id,
            entity.OfficeId,
            entity.Name,
            entity.Signature,
            MaskWebhook(entity.PortalHost),
            entity.PortalHost,
            entity.ValidationStatus,
            entity.ValidationMessage,
            entity.LastValidatedAtUtc,
            entity.IsEnabled,
            integration.ToDto(),
            entity.LeadExportLimit,
            entity.LeadExportSessionCount,
            entity.LeadExportSessionStartedAtUtc,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);
    }

    private static string? MaskWebhook(string? portalHost) =>
        portalHost is null ? null : $"https://{portalHost}/rest/***/";

    private BitrixInstanceIntegrationSettings ResolveIntegrationSettings() =>
        BitrixInstanceIntegrationSettings.FromDefaults(defaultBitrixOptions.Value);

    private static bool CanAccessOffice(OfficeScope scope, Guid? requestedOfficeId, Guid entityOfficeId)
    {
        var (officeId, _) = OfficeIdResolver.Resolve(scope, requestedOfficeId);
        return officeId == entityOfficeId;
    }
}
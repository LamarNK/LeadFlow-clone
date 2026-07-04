using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class OfficeBitrixIntegrationService(
    OrbitaDbContext db,
    WebhookSecretProtector protector,
    BitrixWebhookValidator validator,
    PanelAuditService audit)
{
    public async Task<OfficeBitrixIntegrationDto?> GetForScopeAsync(OfficeScope scope, CancellationToken ct = default)
    {
        if (!scope.HasAccess || scope.IsGlobalAdmin || scope.OfficeId is not Guid officeId)
        {
            return null;
        }

        return await GetForOfficeAsync(officeId, ct);
    }

    public async Task<OfficeBitrixIntegrationDto?> GetForOfficeAsync(Guid officeId, CancellationToken ct = default)
    {
        var office = await db.Offices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == officeId, ct);
        return office is null ? null : MapDto(office);
    }

    public async Task<(OfficeBitrixIntegrationDto? Integration, string? Error)> SaveAsync(
        Guid officeId,
        string webhookUrl,
        string actorUserId,
        string? actorEmail,
        string? ipAddress,
        CancellationToken ct = default)
    {
        var office = await db.Offices.FindAsync([officeId], ct);
        if (office is null)
        {
            return (null, "Офис не найден.");
        }

        var normalized = BitrixWebhookValidator.NormalizeWebhookUrl(webhookUrl);
        if (normalized is null)
        {
            var formatIssue = BitrixWebhookValidator.GetFormatIssue(webhookUrl);
            var error = formatIssue is not null
                ? $"{formatIssue.Value.Message} {formatIssue.Value.Hint}"
                : "Укажите корректную ссылку входящего вебхука Bitrix24.";
            return (null, error);
        }

        var validation = await validator.ValidateAsync(normalized, ct);
        office.BitrixWebhookUrlProtected = protector.Protect(normalized);
        office.BitrixPortalHost = BitrixWebhookValidator.TryGetPortalHost(normalized);
        office.BitrixValidationStatus = validation.Status;
        office.BitrixValidationMessage = validation.Message;
        office.BitrixLastValidatedAtUtc = DateTime.UtcNow;
        office.BitrixUpdatedAtUtc = DateTime.UtcNow;
        office.BitrixUpdatedByUserId = actorUserId;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            actorUserId,
            actorEmail,
            PanelAuditOfficeActions.BitrixWebhookUpdated,
            "office",
            officeId.ToString(),
            validation.Status,
            ipAddress,
            ct);

        return (MapDto(office), null);
    }

    public async Task<(BitrixWebhookValidationDto? Validation, string? Error)> ValidateAsync(
        Guid officeId,
        string? webhookUrl,
        string actorUserId,
        string? actorEmail,
        string? ipAddress,
        bool persistResult,
        CancellationToken ct = default)
    {
        var office = await db.Offices.FindAsync([officeId], ct);
        if (office is null)
        {
            return (null, "Офис не найден.");
        }

        var resolvedUrl = BitrixWebhookValidator.NormalizeWebhookUrl(webhookUrl);
        if (resolvedUrl is null)
        {
            if (string.IsNullOrWhiteSpace(office.BitrixWebhookUrlProtected))
            {
                return (null, "Укажите URL вебхука для проверки.");
            }

            try
            {
                resolvedUrl = protector.Unprotect(office.BitrixWebhookUrlProtected);
            }
            catch
            {
                return (null, "Не удалось прочитать сохранённый вебхук.");
            }
        }

        var validation = await validator.ValidateAsync(resolvedUrl, ct);

        if (persistResult)
        {
            office.BitrixValidationStatus = validation.Status;
            office.BitrixValidationMessage = validation.Message;
            office.BitrixLastValidatedAtUtc = DateTime.UtcNow;
            office.BitrixUpdatedAtUtc = DateTime.UtcNow;
            office.BitrixUpdatedByUserId = actorUserId;
            await db.SaveChangesAsync(ct);
        }

        await audit.LogAsync(
            actorUserId,
            actorEmail,
            PanelAuditOfficeActions.BitrixWebhookValidated,
            "office",
            officeId.ToString(),
            validation.Status,
            ipAddress,
            ct);

        return (validation, null);
    }

    public async Task<string?> ResolveWebhookUrlAsync(Guid officeId, CancellationToken ct = default)
    {
        var office = await db.Offices.AsNoTracking()
            .Where(x => x.Id == officeId)
            .Select(x => new { x.BitrixWebhookUrlProtected, x.BitrixValidationStatus })
            .FirstOrDefaultAsync(ct);

        if (office is null
            || string.IsNullOrWhiteSpace(office.BitrixWebhookUrlProtected)
            || !string.Equals(office.BitrixValidationStatus, BitrixValidationStatuses.Ok, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(office.BitrixWebhookUrlProtected);
        }
        catch
        {
            return null;
        }
    }

    internal static OfficeBitrixIntegrationDto MapDto(OfficeEntity office)
    {
        string? masked = null;
        if (!string.IsNullOrWhiteSpace(office.BitrixWebhookUrlProtected))
        {
            masked = office.BitrixPortalHost is null
                ? "https://***/rest/***/"
                : $"https://{office.BitrixPortalHost}/rest/***/";
        }

        return new OfficeBitrixIntegrationDto(
            office.Id,
            office.Name,
            masked,
            office.BitrixPortalHost,
            office.BitrixValidationStatus,
            office.BitrixValidationMessage,
            office.BitrixLastValidatedAtUtc,
            office.BitrixUpdatedAtUtc,
            office.BitrixTransmissionEnabled);
    }
}
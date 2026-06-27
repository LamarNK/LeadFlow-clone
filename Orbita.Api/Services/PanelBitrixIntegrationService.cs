using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class PanelBitrixIntegrationService(
    OrbitaDbContext db,
    UserManager<IdentityUser> users,
    WebhookSecretProtector protector,
    BitrixWebhookValidator validator,
    PanelAuditService audit)
{
    public async Task<BitrixIntegrationDto?> GetForUserAsync(string targetUserId, CancellationToken ct = default)
    {
        var entity = await db.PanelUserBitrixSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == targetUserId, ct);
        return entity is null ? EmptyDto(targetUserId) : MapDto(targetUserId, entity);
    }

    public async Task<IReadOnlyList<BitrixIntegrationListItemDto>> ListAllAsync(CancellationToken ct = default)
    {
        var panelUsers = await users.Users.AsNoTracking().OrderBy(x => x.Email).ToListAsync(ct);
        var settings = await db.PanelUserBitrixSettings.AsNoTracking().ToListAsync(ct);
        var byUserId = settings.ToDictionary(x => x.UserId, StringComparer.Ordinal);

        var result = new List<BitrixIntegrationListItemDto>();
        foreach (var user in panelUsers)
        {
            var roles = await users.GetRolesAsync(user);
            var role = roles.FirstOrDefault(r => PanelRoles.All.Contains(r, StringComparer.OrdinalIgnoreCase))
                       ?? PanelRoles.Operator;
            byUserId.TryGetValue(user.Id, out var entity);
            result.Add(new BitrixIntegrationListItemDto(
                user.Id,
                user.Email ?? user.UserName ?? string.Empty,
                role,
                entity?.PortalHost,
                entity?.ValidationStatus ?? BitrixValidationStatuses.NotConfigured,
                entity?.ValidationMessage,
                entity?.LastValidatedAtUtc));
        }

        return result;
    }

    public async Task<(BitrixIntegrationDto? Integration, string? Error)> SaveAsync(
        string targetUserId,
        string webhookUrl,
        string actorUserId,
        string? actorEmail,
        string? ipAddress,
        CancellationToken ct = default)
    {
        if (!await users.Users.AnyAsync(x => x.Id == targetUserId, ct))
        {
            return (null, "Пользователь не найден.");
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
        var entity = await db.PanelUserBitrixSettings.FirstOrDefaultAsync(x => x.UserId == targetUserId, ct);
        if (entity is null)
        {
            entity = new PanelUserBitrixSettingsEntity { UserId = targetUserId };
            db.PanelUserBitrixSettings.Add(entity);
        }

        entity.WebhookUrlProtected = protector.Protect(normalized);
        entity.PortalHost = BitrixWebhookValidator.TryGetPortalHost(normalized);
        entity.ValidationStatus = validation.Status;
        entity.ValidationMessage = validation.Message;
        entity.LastValidatedAtUtc = DateTime.UtcNow;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        entity.UpdatedByUserId = actorUserId;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            actorUserId,
            actorEmail,
            PanelAuditActions.BitrixWebhookUpdated,
            "user",
            targetUserId,
            validation.Status,
            ipAddress,
            ct);

        return (MapDto(targetUserId, entity), null);
    }

    public async Task<(BitrixWebhookValidationDto? Validation, string? Error)> ValidateAsync(
        string targetUserId,
        string? webhookUrl,
        string actorUserId,
        string? actorEmail,
        string? ipAddress,
        bool persistResult,
        CancellationToken ct = default)
    {
        string? resolvedUrl = BitrixWebhookValidator.NormalizeWebhookUrl(webhookUrl);
        if (resolvedUrl is null)
        {
            var entity = await db.PanelUserBitrixSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserId == targetUserId, ct);
            if (entity is null || string.IsNullOrWhiteSpace(entity.WebhookUrlProtected))
            {
                return (null, "Укажите URL вебхука для проверки.");
            }

            resolvedUrl = protector.Unprotect(entity.WebhookUrlProtected);
        }

        var validation = await validator.ValidateAsync(resolvedUrl, ct);

        if (persistResult)
        {
            var tracked = await db.PanelUserBitrixSettings.FirstOrDefaultAsync(x => x.UserId == targetUserId, ct);
            if (tracked is not null)
            {
                tracked.ValidationStatus = validation.Status;
                tracked.ValidationMessage = validation.Message;
                tracked.LastValidatedAtUtc = DateTime.UtcNow;
                tracked.UpdatedAtUtc = DateTime.UtcNow;
                tracked.UpdatedByUserId = actorUserId;
                await db.SaveChangesAsync(ct);
            }
        }

        await audit.LogAsync(
            actorUserId,
            actorEmail,
            PanelAuditActions.BitrixWebhookValidated,
            "user",
            targetUserId,
            validation.Status,
            ipAddress,
            ct);

        return (validation, null);
    }

    private static BitrixIntegrationDto EmptyDto(string userId) =>
        new(
            userId,
            null,
            null,
            BitrixValidationStatuses.NotConfigured,
            null,
            null,
            null);

    private BitrixIntegrationDto MapDto(string userId, PanelUserBitrixSettingsEntity entity)
    {
        string? masked = null;
        if (!string.IsNullOrWhiteSpace(entity.WebhookUrlProtected))
        {
            try
            {
                var url = protector.Unprotect(entity.WebhookUrlProtected);
                masked = BitrixWebhookValidator.MaskWebhookUrl(url);
            }
            catch
            {
                masked = entity.PortalHost is null ? null : $"https://{entity.PortalHost}/rest/***/";
            }
        }

        return new(
            userId,
            masked,
            entity.PortalHost,
            entity.ValidationStatus,
            entity.ValidationMessage,
            entity.LastValidatedAtUtc,
            entity.UpdatedAtUtc);
    }
}
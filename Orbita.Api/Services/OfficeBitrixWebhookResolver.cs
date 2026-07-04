using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class OfficeBitrixWebhookResolver(
    OrbitaDbContext db,
    UserManager<IdentityUser> users,
    WebhookSecretProtector protector,
    OfficeBitrixIntegrationService officeBitrixIntegration)
{
    public async Task<string?> ResolvePrimaryForIngestionAsync(Guid officeId, CancellationToken ct = default)
    {
        var officeWebhook = await officeBitrixIntegration.ResolveWebhookUrlAsync(officeId, ct);
        if (!string.IsNullOrWhiteSpace(officeWebhook))
        {
            return officeWebhook;
        }

        var entries = await ListOfficeWebhooksAsync(officeId, ct);
        var primary = entries.FirstOrDefault(x =>
            x.IsPrimaryForIngestion
            && string.Equals(x.ValidationStatus, BitrixValidationStatuses.Ok, StringComparison.Ordinal));
        return primary?.WebhookUrl;
    }

    public async Task<IReadOnlyList<OfficeBitrixWebhookEntry>> ListOfficeWebhooksAsync(
        Guid officeId,
        CancellationToken ct = default)
    {
        var profiles = await db.PanelUserProfiles
            .AsNoTracking()
            .Where(x => x.OfficeId == officeId)
            .Select(x => x.UserId)
            .ToListAsync(ct);

        if (profiles.Count == 0)
        {
            return [];
        }

        var settings = await db.PanelUserBitrixSettings
            .AsNoTracking()
            .Where(x => profiles.Contains(x.UserId))
            .ToListAsync(ct);

        if (settings.Count == 0)
        {
            return [];
        }

        var entries = new List<OfficeBitrixWebhookEntry>();
        foreach (var entity in settings.OrderBy(x => x.UserId, StringComparer.Ordinal))
        {
            var user = await users.FindByIdAsync(entity.UserId);
            if (user is null)
            {
                continue;
            }

            string? webhookUrl = null;
            if (!string.IsNullOrWhiteSpace(entity.WebhookUrlProtected))
            {
                try
                {
                    webhookUrl = protector.Unprotect(entity.WebhookUrlProtected);
                }
                catch
                {
                    webhookUrl = null;
                }
            }

            entries.Add(new OfficeBitrixWebhookEntry(
                entity.UserId,
                user.Email ?? user.UserName ?? string.Empty,
                entity.PortalHost,
                entity.ValidationStatus,
                webhookUrl));
        }

        var primaryUserId = entries
            .Where(x => string.Equals(x.ValidationStatus, BitrixValidationStatuses.Ok, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(x.WebhookUrl))
            .OrderBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.UserId)
            .FirstOrDefault();

        return entries
            .Select(x => x with { IsPrimaryForIngestion = x.UserId == primaryUserId })
            .ToList();
    }

    public async Task<IReadOnlyList<OfficeBitrixWebhookDto>> ListOfficeWebhookDtosAsync(
        Guid officeId,
        CancellationToken ct = default)
    {
        var entries = await ListOfficeWebhooksAsync(officeId, ct);
        return entries
            .Select(x => new OfficeBitrixWebhookDto(
                x.UserId,
                x.Email,
                x.PortalHost,
                x.ValidationStatus,
                x.IsPrimaryForIngestion))
            .ToList();
    }

    public async Task<string?> ResolvePortalHostAsync(Guid officeId, CancellationToken ct = default)
    {
        var office = await db.Offices.AsNoTracking()
            .Where(x => x.Id == officeId)
            .Select(x => new { x.BitrixPortalHost, x.BitrixValidationStatus })
            .FirstOrDefaultAsync(ct);

        if (office is not null
            && string.Equals(office.BitrixValidationStatus, BitrixValidationStatuses.Ok, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(office.BitrixPortalHost))
        {
            return office.BitrixPortalHost;
        }

        var entries = await ListOfficeWebhooksAsync(officeId, ct);
        return entries.FirstOrDefault(x => x.IsPrimaryForIngestion && !string.IsNullOrWhiteSpace(x.PortalHost))?.PortalHost
            ?? entries.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.PortalHost))?.PortalHost;
    }
}

public sealed record OfficeBitrixWebhookEntry(
    string UserId,
    string Email,
    string? PortalHost,
    string ValidationStatus,
    string? WebhookUrl,
    bool IsPrimaryForIngestion = false);
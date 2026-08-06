using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class NavBadgesService(
    OrbitaApiClient api,
    IOptions<DesignPreviewOptions> previewOptions,
    IOfficeContext officeContext,
    IHttpContextAccessor httpContextAccessor)
{
    public async Task<NavBadgesDto> GetAsync(CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            var summary = DesignPreviewData.Summary;
            var canReadPreviewCrmNotifications = officeContext.EffectiveOfficeId is Guid
                && httpContextAccessor.HttpContext?.User.HasClaim(
                    PanelPermissions.ClaimType,
                    PanelPermissions.Crm) == true;
            var previewCrmNotifications = canReadPreviewCrmNotifications
                ? DesignPreviewData.GetCrmTaskNotificationSummary()
                : null;
            return new NavBadgesDto(
                summary.Errors,
                summary.SentToCrm,
                summary.ActionRequired,
                DateTime.UtcNow,
                previewCrmNotifications?.Enabled == true ? previewCrmNotifications.UnreadCount : 0,
                previewCrmNotifications?.Enabled);
        }

        var summaryTask = api.GetSummaryAsync(ct);
        var canReadCrmNotifications = officeContext.EffectiveOfficeId is Guid
            && httpContextAccessor.HttpContext?.User.HasClaim(
                PanelPermissions.ClaimType,
                PanelPermissions.Crm) == true;
        var crmNotificationsTask = canReadCrmNotifications
            ? api.GetCrmTaskNotificationSummaryAsync(ct)
            : Task.FromResult<CrmTaskNotificationSummaryDto?>(null);
        await Task.WhenAll(summaryTask, crmNotificationsTask);
        var live = await summaryTask;
        var crmNotifications = await crmNotificationsTask;
        if (live is null)
        {
            return new NavBadgesDto(
                0,
                0,
                0,
                DateTime.UtcNow,
                crmNotifications?.Enabled == true ? crmNotifications.UnreadCount : 0,
                crmNotifications?.Enabled);
        }

        return new NavBadgesDto(
            live.Errors,
            live.SentToCrm,
            live.ActionRequired,
            live.AggregatedAtUtc,
            crmNotifications?.Enabled == true ? crmNotifications.UnreadCount : 0,
            crmNotifications?.Enabled);
    }
}

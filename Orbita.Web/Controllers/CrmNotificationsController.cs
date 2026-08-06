using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize(Policy = PanelPermissions.CrmTasks)]
[ApiController]
[Route("Crm/Notifications")]
public sealed class CrmNotificationsController(OrbitaApiClient api) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(bool unreadOnly = false, int limit = 20, CancellationToken ct = default)
    {
        var notifications = await api.GetCrmTaskNotificationsAsync(unreadOnly, limit, ct);
        return notifications is null
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Не удалось загрузить уведомления CRM." })
            : Ok(notifications);
    }

    [HttpPost("{notificationId:guid}/read")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid notificationId, CancellationToken ct = default)
    {
        var (success, error) = await api.MarkCrmTaskNotificationReadAsync(notificationId, ct);
        return success ? NoContent() : BadRequest(new { error });
    }

    [HttpPost("read-all")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct = default)
    {
        var (success, error) = await api.MarkAllCrmTaskNotificationsReadAsync(ct);
        return success ? NoContent() : BadRequest(new { error });
    }
}

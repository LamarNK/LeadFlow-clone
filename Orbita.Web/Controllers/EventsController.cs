using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class EventsController(IEventsService events) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        string? q,
        string? type,
        Guid? workerId,
        string? account,
        string? level,
        int page = 1,
        CancellationToken ct = default)
    {
        var model = await events.GetIndexAsync(q, type, workerId, account, level, page, ct: ct);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dismiss(Guid eventId, CancellationToken ct)
    {
        var (success, error) = await events.DismissEventAsync(eventId, ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось отметить событие." });
        }

        return Ok(new { message = "Событие отмечено как обработанное." });
    }
}
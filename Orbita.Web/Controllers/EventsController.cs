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
}
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class EventsController(IEventsService events) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Snapshot(
        string? q,
        string? type,
        Guid? workerId,
        Guid? accountId,
        string? level,
        string? view,
        string? severity,
        int page = 1,
        CancellationToken ct = default)
    {
        var model = await events.GetIndexAsync(q, type, workerId, accountId, level, view, severity, page, ct);
        if (model.JournalView == "errors" && model.ErrorsPage is not null)
        {
            return Json(new ErrorsLiveSnapshotViewModel
            {
                UpdatedAtUtc = model.ErrorsPage.Header.UpdatedAtUtc,
                KpiCards = model.ErrorsPage.KpiCards,
                Errors = model.ErrorsPage.Errors,
                Pagination = model.ErrorsPage.Pagination
            });
        }

        return Json(new EventsLiveSnapshotViewModel
        {
            UpdatedAtUtc = model.Header.UpdatedAtUtc,
            KpiCards = model.KpiCards,
            Events = model.Events,
            Pagination = model.Pagination
        });
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? q,
        string? type,
        Guid? workerId,
        Guid? accountId,
        string? level,
        string? view,
        string? severity,
        int page = 1,
        CancellationToken ct = default)
    {
        var model = await events.GetIndexAsync(q, type, workerId, accountId, level, view, severity, page, ct: ct);
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
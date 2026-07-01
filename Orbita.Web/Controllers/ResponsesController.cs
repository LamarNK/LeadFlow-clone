using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class ResponsesController(IResponsesService responses) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Snapshot(
        string? from,
        string? to,
        string? status,
        Guid? workerId,
        Guid? accountId,
        string? vacancy,
        string? search,
        int page = 1,
        CancellationToken ct = default)
    {
        var model = await responses.GetIndexAsync(
            from, to, status, workerId, accountId, vacancy, search, selectedId: null, page, ct);

        return Json(new ResponsesLiveSnapshotViewModel
        {
            UpdatedAtUtc = model.Header.UpdatedAtUtc,
            KpiCards = model.KpiCards,
            Responses = model.Responses,
            Pagination = model.Pagination
        });
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? from,
        string? to,
        string? status,
        Guid? workerId,
        Guid? accountId,
        string? vacancy,
        string? search,
        Guid? id,
        int page = 1,
        CancellationToken ct = default)
    {
        var model = await responses.GetIndexAsync(
            from, to, status, workerId, accountId, vacancy, search, id, page, ct);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resend(
        Guid id,
        string? from,
        string? to,
        string? status,
        Guid? workerId,
        Guid? accountId,
        string? vacancy,
        string? search,
        int page = 1,
        CancellationToken ct = default)
    {
        var (success, error) = await responses.ResendToBitrixAsync(id, ct);
        if (!success)
        {
            TempData["ResponsesError"] = error;
        }

        return RedirectToAction(nameof(Index), new
        {
            from,
            to,
            status,
            workerId,
            accountId,
            vacancy,
            search,
            page,
            id
        });
    }
}
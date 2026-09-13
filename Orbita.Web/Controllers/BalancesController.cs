using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize(Policy = PanelPermissions.Balances)]
public sealed class BalancesController(
    IBalancesService balances,
    IWorkersService workers) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        string? tab = null,
        string? q = null,
        bool history = false,
        int page = 1,
        [FromQuery] Guid[]? excludedWorkerIds = null,
        CancellationToken ct = default)
    {
        var model = await balances.GetIndexAsync(
            tab,
            q,
            history || tab == "history",
            page,
            excludedWorkerIds,
            ct);
        ViewData["BalancesTab"] = tab ?? "low";
        ViewData["BalancesQuery"] = q;
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Snapshot(
        string? tab = null,
        string? q = null,
        bool history = false,
        int page = 1,
        [FromQuery] Guid[]? excludedWorkerIds = null,
        CancellationToken ct = default) =>
        Json(await balances.GetIndexAsync(
            tab,
            q,
            history || tab == "history",
            page,
            excludedWorkerIds,
            ct));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateBatch(
        [FromForm] IReadOnlyList<BalanceTopUpSelection> selections,
        CancellationToken ct = default)
    {
        var results = new List<object>();
        foreach (var group in selections.GroupBy(x => x.WorkerId))
        {
            foreach (var selection in group)
            {
                var result = await workers.CreateTopUpSessionAsync(
                    selection.WorkerId,
                    selection.AccountId,
                    selection.SubProfileId,
                    ct);
                results.Add(new
                {
                    selection.WorkerId,
                    selection.AccountId,
                    selection.SubProfileId,
                    success = result.Session is not null,
                    status = result.Session?.Status,
                    error = result.Error,
                    conflictSessionId = result.ConflictSessionId
                });
            }
        }

        return Ok(new { results });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(Guid sessionId, CancellationToken ct)
    {
        var result = await workers.MarkTopUpSessionPaidAsync(sessionId, ct);
        return result.Success
            ? Ok(new { message = "Оплата отмечена. На следующем проходе проверим историю операций Avito." })
            : BadRequest(new { error = result.Error });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid sessionId, CancellationToken ct)
    {
        var result = await workers.CancelTopUpSessionAsync(sessionId, ct);
        return result.Success ? Ok() : BadRequest(new { error = result.Error });
    }
}

public sealed record BalanceTopUpSelection(Guid WorkerId, Guid AccountId, string SubProfileId);

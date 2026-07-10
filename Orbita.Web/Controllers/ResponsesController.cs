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
        int? pageSize = null,
        string? sort = null,
        string? dir = null,
        CancellationToken ct = default)
    {
        var model = await responses.GetIndexAsync(
            from, to, status, workerId, accountId, vacancy, search, selectedId: null, page, pageSize, sort, dir, ct);

        return Json(new ResponsesLiveSnapshotViewModel
        {
            UpdatedAtUtc = model.Header.UpdatedAtUtc,
            KpiCards = model.KpiCards,
            Responses = model.Responses,
            Pagination = model.Pagination
        });
    }

    [HttpGet]
    public async Task<IActionResult> DetailJson(Guid id, CancellationToken ct = default)
    {
        var json = await responses.GetDetailJsonAsync(id, ct);
        if (json is null)
        {
            return NotFound();
        }

        return Json(json);
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
        int? pageSize = null,
        string? sort = null,
        string? dir = null,
        CancellationToken ct = default)
    {
        var model = await responses.GetIndexAsync(
            from, to, status, workerId, accountId, vacancy, search, id, page, pageSize, sort, dir, ct);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkSend(BulkSendResponsesToBitrixFormModel model, CancellationToken ct = default)
    {
        if (model.ResponseIds.Count == 0 || model.BitrixInstanceId == Guid.Empty)
        {
            return BadRequest(new { error = "Выберите отклики и Битрикс для отправки." });
        }

        var (result, error) = await responses.BulkSendToBitrixAsync(model.ResponseIds, model.BitrixInstanceId, ct);
        if (result is null)
        {
            return BadRequest(new { error = error ?? "Не удалось выполнить массовую отправку." });
        }

        return Json(result);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(SendResponseToBitrixFormModel model, CancellationToken ct = default)
    {
        if (model.Id == Guid.Empty || model.BitrixInstanceId == Guid.Empty)
        {
            TempData["ResponsesError"] = "Укажите отклик и Битрикс для отправки.";
            return RedirectToAction(nameof(Index), new
            {
                model.From,
                model.To,
                status = model.Status,
                workerId = model.WorkerId,
                accountId = model.AccountId,
                vacancy = model.Vacancy,
                search = model.Search,
                page = model.Page,
                id = model.Id == Guid.Empty ? (Guid?)null : model.Id,
                sort = model.Sort,
                dir = model.Dir
            });
        }

        var (success, error) = await responses.SendToBitrixAsync(model.Id, model.BitrixInstanceId, ct);
        if (!success)
        {
            TempData["ResponsesError"] = error;
        }

        return RedirectToAction(nameof(Index), new
        {
            model.From,
            model.To,
            status = model.Status,
            workerId = model.WorkerId,
            accountId = model.AccountId,
            vacancy = model.Vacancy,
            search = model.Search,
            page = model.Page,
            id = model.Id,
            sort = model.Sort,
            dir = model.Dir
        });
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
        string? sort = null,
        string? dir = null,
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
            id,
            sort,
            dir
        });
    }
}
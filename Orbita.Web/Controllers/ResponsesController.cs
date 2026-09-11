using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Orbita.Contracts;
using Orbita.Web.Helpers;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize(Policy = PanelPermissions.Responses)]
public sealed class ResponsesController(IResponsesService responses) : Controller
{
    [HttpGet("/Responses/{id:guid}/Avatar")]
    public async Task<IActionResult> Avatar(Guid id, CancellationToken ct = default)
    {
        var result = await responses.GetAvatarAsync(id, ct);
        return result.Stream is null
            ? NotFound()
            : File(result.Stream, result.ContentType ?? "image/jpeg");
    }

    [HttpGet]
    public async Task<IActionResult> Snapshot(
        string? from,
        string? to,
        string? status,
        Guid? workerId,
        Guid[]? workerIds,
        Guid? accountId,
        Guid[]? accountIds,
        string[]? bitrixDestination,
        string? gender,
        int? ageFrom,
        int? ageTo,
        string? vacancy,
        string? search,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? dir = null,
        CancellationToken ct = default)
    {
        var model = await responses.GetIndexAsync(
            from, to, status, workerId, accountId, bitrixDestination: null, gender, ageFrom, ageTo, vacancy, search, selectedId: null, page, pageSize, sort, dir, ct, workerIds, accountIds, bitrixDestination);

        return Json(new ResponsesLiveSnapshotViewModel
        {
            UpdatedAtUtc = model.Header.UpdatedAtUtc,
            KpiCards = model.KpiCards,
            Responses = model.Responses,
            Pagination = model.Pagination,
            DeliveryOffices = model.DeliveryOffices,
            SendBitrixInstances = model.SendBitrixInstances
        });
    }

    [HttpGet]
    public async Task<IActionResult> DeliverOptions(CancellationToken ct = default)
    {
        var options = await responses.GetDeliverOptionsAsync(ct);
        return Json(options);
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(UpdateResponseFormModel model, CancellationToken ct = default)
    {
        if (model.Id == Guid.Empty)
        {
            return BadRequest(new { error = "Укажите отклик." });
        }

        var (success, error) = await responses.UpdateAsync(
            model.Id,
            model.FullName ?? string.Empty,
            model.PhoneRaw ?? string.Empty,
            model.City ?? string.Empty,
            model.Age,
            model.Gender,
            ct);

        return success
            ? Ok(new { success = true })
            : BadRequest(new { error = error ?? "Не удалось сохранить отклик." });
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? from,
        string? to,
        string? status,
        Guid? workerId,
        Guid[]? workerIds,
        Guid? accountId,
        Guid[]? accountIds,
        string[]? bitrixDestination,
        string? gender,
        int? ageFrom,
        int? ageTo,
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
            from, to, status, workerId, accountId, bitrixDestination: null, gender, ageFrom, ageTo, vacancy, search, id, page, pageSize, sort, dir, ct, workerIds, accountIds, bitrixDestination);
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
    public async Task<IActionResult> BulkDeliver(BulkDeliverResponsesFormModel model, CancellationToken ct = default)
    {
        if (model.ResponseIds.Count == 0)
        {
            return BadRequest(new { error = "Выберите отклики." });
        }

        // Checkboxes / form flags: read explicitly so defaults do not force CRM on.
        var toCrm = FormBindingHelper.ReadCheckbox(Request.Form, "ToCrm");
        var toBitrix = FormBindingHelper.ReadCheckbox(Request.Form, "ToBitrix");

        if (!toCrm && !toBitrix)
        {
            return BadRequest(new { error = "Выберите канал: CRM и/или Bitrix." });
        }

        var officeIds = CollectIds(model.OfficeIds, model.OfficeId);
        var bitrixIds = CollectIds(model.BitrixInstanceIds, model.BitrixInstanceId);

        var (result, error) = await responses.DeliverBulkAsync(
            model.ResponseIds,
            officeIds,
            toCrm,
            toBitrix,
            bitrixIds,
            ct);
        if (result is null)
        {
            return BadRequest(new { error = error ?? "Не удалось выполнить массовую отправку." });
        }

        return Json(result);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deliver(DeliverResponseFormModel model, CancellationToken ct = default)
    {
        var wantsJson = Request.Headers.Accept.ToString()
            .Contains("application/json", StringComparison.OrdinalIgnoreCase);

        // Unchecked checkboxes are omitted from form posts.
        var toCrm = FormBindingHelper.ReadCheckbox(Request.Form, "ToCrm");
        var toBitrix = FormBindingHelper.ReadCheckbox(Request.Form, "ToBitrix");

        if (model.Id == Guid.Empty)
        {
            if (wantsJson)
            {
                return BadRequest(new { error = "Укажите отклик." });
            }

            TempData["ResponsesError"] = "Укажите отклик.";
            return RedirectToAction(nameof(Index), BuildRedirect(model));
        }

        if (!toCrm && !toBitrix)
        {
            if (wantsJson)
            {
                return BadRequest(new { error = "Выберите канал: CRM и/или Bitrix." });
            }

            TempData["ResponsesError"] = "Выберите канал: CRM и/или Bitrix.";
            return RedirectToAction(nameof(Index), BuildRedirect(model));
        }

        var officeIds = CollectIds(model.OfficeIds, model.OfficeId);
        var bitrixIds = CollectIds(model.BitrixInstanceIds, model.BitrixInstanceId);

        var (success, error) = await responses.DeliverAsync(
            model.Id,
            officeIds,
            toCrm,
            toBitrix,
            bitrixIds,
            ct);
        if (wantsJson)
        {
            return success
                ? Ok(new { success = true })
                : BadRequest(new { error = error ?? "Не удалось отправить отклик." });
        }

        if (!success)
        {
            TempData["ResponsesError"] = error;
        }

        return RedirectToAction(nameof(Index), BuildRedirect(model));
    }

    private static List<Guid> CollectIds(IReadOnlyList<Guid>? ids, Guid? single)
    {
        var list = new List<Guid>();
        if (ids is not null)
        {
            list.AddRange(ids.Where(id => id != Guid.Empty));
        }

        if (single is Guid one && one != Guid.Empty && !list.Contains(one))
        {
            list.Add(one);
        }

        return list;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(SendResponseToBitrixFormModel model, CancellationToken ct = default)
    {
        // Legacy: Bitrix-only send still supported.
        if (model.Id == Guid.Empty || model.BitrixInstanceId == Guid.Empty)
        {
            TempData["ResponsesError"] = "Укажите отклик и Битрикс для отправки.";
            return RedirectToAction(nameof(Index), BuildSendRedirect(model, includeEmptyId: true));
        }

        var (success, error) = await responses.SendToBitrixAsync(model.Id, model.BitrixInstanceId, ct);
        if (!success)
        {
            TempData["ResponsesError"] = error;
        }

        return RedirectToAction(nameof(Index), BuildSendRedirect(model, includeEmptyId: false));
    }

    private static RouteValueDictionary BuildSendRedirect(SendResponseToBitrixFormModel model, bool includeEmptyId)
    {
        var id = includeEmptyId && model.Id == Guid.Empty ? (Guid?)null : model.Id;
        return BuildFilterRedirect(
            model.From,
            model.To,
            model.Status,
            model.WorkerId,
            model.WorkerIds,
            model.AccountId,
            model.AccountIds,
            model.BitrixDestination,
            model.BitrixDestinations,
            model.Gender,
            model.AgeFrom,
            model.AgeTo,
            model.Vacancy,
            model.Search,
            model.Page,
            id,
            model.Sort,
            model.Dir);
    }

    private static RouteValueDictionary BuildRedirect(DeliverResponseFormModel model) =>
        BuildFilterRedirect(
            model.From,
            model.To,
            model.Status,
            model.WorkerId,
            model.WorkerIds,
            model.AccountId,
            model.AccountIds,
            model.BitrixDestination,
            model.BitrixDestinations,
            model.Gender,
            model.AgeFrom,
            model.AgeTo,
            model.Vacancy,
            model.Search,
            model.Page,
            model.Id == Guid.Empty ? null : model.Id,
            model.Sort,
            model.Dir);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resend(
        Guid id,
        string? from,
        string? to,
        string? status,
        Guid? workerId,
        Guid[]? workerIds,
        Guid? accountId,
        Guid[]? accountIds,
        string[]? bitrixDestination,
        string? gender,
        int? ageFrom,
        int? ageTo,
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

        return RedirectToAction(nameof(Index), BuildFilterRedirect(
            from,
            to,
            status,
            workerId,
            workerIds,
            accountId,
            accountIds,
            bitrixDestination: null,
            bitrixDestinations: bitrixDestination,
            gender,
            ageFrom,
            ageTo,
            vacancy,
            search,
            page,
            id,
            sort,
            dir));
    }

    private static RouteValueDictionary BuildFilterRedirect(
        string? from,
        string? to,
        string? status,
        Guid? workerId,
        IReadOnlyList<Guid>? workerIds,
        Guid? accountId,
        IReadOnlyList<Guid>? accountIds,
        string? bitrixDestination,
        IReadOnlyList<string>? bitrixDestinations,
        string? gender,
        int? ageFrom,
        int? ageTo,
        string? vacancy,
        string? search,
        int page,
        Guid? id,
        string? sort,
        string? dir)
    {
        var workers = ResponseCatalogFilterValues.MergeIds(workerIds, workerId);
        var accounts = ResponseCatalogFilterValues.MergeIds(accountIds, accountId);
        var destinations = ResponseCatalogFilterValues.MergeValues(bitrixDestinations, bitrixDestination);
        var route = new RouteValueDictionary
        {
            ["from"] = from,
            ["to"] = to,
            ["status"] = status,
            ["gender"] = gender,
            ["ageFrom"] = ageFrom,
            ["ageTo"] = ageTo,
            ["vacancy"] = vacancy,
            ["search"] = search,
            ["page"] = page,
            ["id"] = id,
            ["sort"] = sort,
            ["dir"] = dir
        };

        if (workers.Count == 1)
        {
            route["workerId"] = workers[0];
        }
        else if (workers.Count > 1)
        {
            route["workerIds"] = workers;
        }

        if (accounts.Count == 1)
        {
            route["accountId"] = accounts[0];
        }
        else if (accounts.Count > 1)
        {
            route["accountIds"] = accounts;
        }

        if (destinations.Count == 1)
        {
            route["bitrixDestination"] = destinations[0];
        }
        else if (destinations.Count > 1)
        {
            route["bitrixDestination"] = destinations;
        }

        return route;
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize(Policy = PanelPermissions.Listings)]
public sealed class ListingsController(IListingsService listings) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        string? q,
        string? tab,
        Guid? workerId,
        Guid[]? workerIds,
        Guid? accountId,
        Guid[]? accountIds,
        string? subProfileId,
        string[]? subProfileIds,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? dir = null,
        CancellationToken ct = default)
    {
        var model = await listings.GetIndexAsync(
            q,
            tab,
            ResponseCatalogFilterValues.MergeIds(workerIds, workerId),
            ResponseCatalogFilterValues.MergeIds(accountIds, accountId),
            ResponseCatalogFilterValues.MergeValues(subProfileIds, subProfileId),
            page,
            pageSize,
            sort,
            dir,
            ct);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Snapshot(
        string? q,
        string? tab,
        Guid? workerId,
        Guid[]? workerIds,
        Guid? accountId,
        Guid[]? accountIds,
        string? subProfileId,
        string[]? subProfileIds,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? dir = null,
        CancellationToken ct = default)
    {
        var model = await listings.GetIndexAsync(
            q,
            tab,
            ResponseCatalogFilterValues.MergeIds(workerIds, workerId),
            ResponseCatalogFilterValues.MergeIds(accountIds, accountId),
            ResponseCatalogFilterValues.MergeValues(subProfileIds, subProfileId),
            page,
            pageSize,
            sort,
            dir,
            ct);
        return Json(new ListingsLiveSnapshotViewModel
        {
            UpdatedAtUtc = model.Header.UpdatedAtUtc,
            KpiCards = model.KpiCards,
            Rows = model.Rows,
            AccountScopes = model.AccountScopes,
            Pagination = model.Pagination
        });
    }
}

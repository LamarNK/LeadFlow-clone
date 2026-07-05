using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class AccountsController(IAccountsService accounts, IWorkersService workers) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Snapshot(
        string? q,
        string? tab,
        Guid? workerId,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? dir = null,
        CancellationToken ct = default)
    {
        var model = await accounts.GetIndexAsync(q, tab, workerId, page, pageSize, sort, dir, ct);
        return Json(new AccountsLiveSnapshotViewModel
        {
            UpdatedAtUtc = model.Header.UpdatedAtUtc,
            KpiCards = model.KpiCards,
            Accounts = model.Accounts,
            Pagination = model.Pagination
        });
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? q,
        string? tab,
        Guid? workerId,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? dir = null,
        CancellationToken ct = default)
    {
        var model = await accounts.GetIndexAsync(q, tab, workerId, page, pageSize, sort, dir, ct);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSubProfile(
        Guid workerId,
        Guid accountId,
        string subProfileId,
        bool isEnabledInPanel,
        CancellationToken ct)
    {
        var (success, error) = await workers.UpdateSubProfileEnabledAsync(
            workerId, accountId, subProfileId, isEnabledInPanel, ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось сохранить." });
        }

        return Ok(new
        {
            message = isEnabledInPanel ? "Субпрофиль включён." : "Субпрофиль отключён.",
            isEnabledInPanel
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshSubProfiles(Guid workerId, Guid accountId, CancellationToken ct)
    {
        var (success, error) = await workers.RequestSubProfilesRefreshAsync(workerId, accountId, ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось отправить запрос." });
        }

        return Ok(new { message = "Воркер обновит субпрофили при следующем цикле." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(Guid workerId, Guid accountId, bool enabled, CancellationToken ct)
    {
        var (success, error) = await workers.UpdateWorkerAccountAsync(workerId, accountId, enabled, ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось изменить статус аккаунта." });
        }

        return Ok(new { message = enabled ? "Аккаунт включён в панели." : "Аккаунт отключён в панели." });
    }
}
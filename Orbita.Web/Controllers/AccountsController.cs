using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class AccountsController(IAccountsService accounts, IWorkersService workers) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? q, string? tab, int page = 1, CancellationToken ct = default)
    {
        var model = await accounts.GetIndexAsync(q, tab, page, ct);
        return View(model);
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
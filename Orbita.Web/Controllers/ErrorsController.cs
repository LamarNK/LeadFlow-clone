using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize(Policy = PanelPermissions.Events)]
public sealed class ErrorsController(IErrorsService errors) : Controller
{
    [HttpGet]
    public IActionResult Index(
        string? q,
        string? severity,
        string? type,
        Guid? workerId,
        Guid? accountId,
        Guid? account,
        int page = 1) =>
        RedirectToAction("Index", "Events", new
        {
            view = "errors",
            q,
            severity,
            type,
            workerId,
            accountId = accountId ?? account,
            page
        });

    [HttpGet]
    public IActionResult Snapshot(
        string? q,
        string? severity,
        string? type,
        Guid? workerId,
        Guid? accountId,
        Guid? account,
        int page = 1) =>
        RedirectToAction(nameof(Index), new { q, severity, type, workerId, accountId = accountId ?? account, page });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dismiss(Guid eventId, CancellationToken ct)
    {
        var (success, error) = await errors.DismissEventAsync(eventId, ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось отметить ошибку." });
        }

        return Ok(new { message = "Ошибка отмечена как обработанная." });
    }
}

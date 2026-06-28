using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class ErrorsController(IErrorsService errors) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        string? q,
        string? severity,
        string? type,
        Guid? workerId,
        string? account,
        int page = 1,
        CancellationToken ct = default)
    {
        var model = await errors.GetIndexAsync(q, severity, type, workerId, account, page, ct);
        return View(model);
    }

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
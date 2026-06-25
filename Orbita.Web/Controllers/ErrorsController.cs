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
}
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class WorkersController(IWorkersService workers) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? q, int page = 1, CancellationToken ct = default)
    {
        var model = await workers.GetIndexAsync(q, page, ct);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var model = await workers.GetDetailsAsync(id, ct);
        return model is null ? NotFound() : View(model);
    }
}
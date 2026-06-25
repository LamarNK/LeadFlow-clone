using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class AccountsController(IAccountsService accounts) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? q, string? tab, int page = 1, CancellationToken ct = default)
    {
        var model = await accounts.GetIndexAsync(q, tab, page, ct);
        return View(model);
    }
}
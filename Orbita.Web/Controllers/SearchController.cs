using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
[ApiController]
[Route("Search")]
public sealed class SearchController(GlobalSearchService search) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] string? q, [FromQuery] int limit = 8, CancellationToken ct = default) =>
        Ok(await search.SearchAsync(q, limit, ct));
}
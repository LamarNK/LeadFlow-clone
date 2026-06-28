using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
[ApiController]
[Route("Nav")]
public sealed class NavController(NavBadgesService badges) : ControllerBase
{
    [HttpGet("Badges")]
    public async Task<IActionResult> Badges(CancellationToken ct) =>
        Ok(await badges.GetAsync(ct));
}
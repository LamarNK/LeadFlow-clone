using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class DiagnosticsController(OrbitaApiClient api) : Controller
{
    [HttpGet("/Diagnostics/Image/{id:guid}")]
    public async Task<IActionResult> Image(Guid id, CancellationToken ct)
    {
        var result = await api.GetDiagnosticImageAsync(id, ct);
        if (result.Stream is null)
        {
            return NotFound();
        }

        return File(result.Stream, result.ContentType ?? "image/png");
    }
}
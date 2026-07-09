using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class BrowserMonitorController(OrbitaApiClient api) : Controller
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start([FromQuery] Guid workerId, CancellationToken ct)
    {
        var (session, error) = await api.StartBrowserMonitorSessionAsync(workerId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return BadRequest(new { error = error ?? "Не удалось создать сессию просмотра." });
        }

        return Ok(session);
    }

    [HttpPost("Stop/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    {
        var (success, error) = await api.StopBrowserMonitorSessionAsync(id, ct);
        return success ? Ok() : BadRequest(new { error = error ?? "Не удалось завершить сессию." });
    }
}
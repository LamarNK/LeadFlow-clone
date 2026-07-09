using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class CaptchaController(OrbitaApiClient api) : Controller
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start([FromForm] CreateCaptchaSessionRequest request, CancellationToken ct)
    {
        var (session, conflict, error) = await api.CreateCaptchaSessionAsync(request, ct).ConfigureAwait(false);
        if (conflict is not null)
        {
            return Conflict(new
            {
                error = conflict.Message,
                activeOperatorDisplayName = conflict.ActiveOperatorDisplayName,
                activeAccountName = conflict.ActiveAccountName
            });
        }

        if (session is null)
        {
            return BadRequest(new { error = error ?? "Не удалось создать сессию." });
        }

        return Ok(session);
    }

    [HttpPost("Cancel/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var (success, error) = await api.CancelCaptchaSessionAsync(id, ct);
        return success ? Ok() : BadRequest(new { error = error ?? "Не удалось отменить сессию." });
    }

    [HttpGet]
    public async Task<IActionResult> WorkerLock(Guid workerId, CancellationToken ct)
    {
        var lockState = await api.GetWorkerCaptchaLockAsync(workerId, ct);
        return lockState is null ? NotFound() : Ok(lockState);
    }
}
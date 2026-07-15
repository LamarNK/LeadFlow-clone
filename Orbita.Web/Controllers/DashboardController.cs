using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class DashboardController(
    IDashboardService dashboard,
    IWorkersService workers,
    AuthSession session,
    OrbitaAuthService auth) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? from, string? to, CancellationToken ct)
    {
        var period = DashboardPeriod.Parse(from, to);
        var model = await dashboard.GetDashboardAsync(period, ct);
        if (!string.IsNullOrWhiteSpace(model.ErrorMessage) && IsApiSessionMissing())
        {
            await auth.SignOutAsync(ct);
            return RedirectToAction("Login", "Account");
        }

        return View(model);
    }

    private bool IsApiSessionMissing() =>
        string.IsNullOrWhiteSpace(session.Token)
        || session.Token.Count(c => c == '.') < 2;

    [HttpGet]
    public async Task<IActionResult> Snapshot(string? from, string? to, CancellationToken ct)
    {
        var period = DashboardPeriod.Parse(from, to);
        var model = await dashboard.GetDashboardAsync(period, ct);
        if (!string.IsNullOrWhiteSpace(model.ErrorMessage))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = model.ErrorMessage });
        }

        return Json(new DashboardLiveSnapshotViewModel
        {
            UpdatedAtUtc = model.Header.UpdatedAtUtc,
            KpiCards = model.KpiCards,
            Workers = model.Workers,
            Events = model.Events,
            AccountStats = model.AccountStats,
            Charts = model.Charts,
            EnabledWorkersCount = model.EnabledWorkersCount,
            DisabledWorkersCount = model.DisabledWorkersCount,
            ShowWorkersMonitoringControls = model.ShowWorkersMonitoringControls
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableAllWorkers(CancellationToken ct)
    {
        var (result, error) = await workers.SetAllWorkersMonitoringAsync(true, ct);
        if (error is not null || result is null)
        {
            return BadRequest(new { error = error ?? "Не удалось включить мониторинг." });
        }

        return Ok(new
        {
            message = result.UpdatedCount > 0
                ? $"Мониторинг включён на {result.UpdatedCount} воркерах."
                : "Все воркеры уже были включены.",
            result
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableAllWorkers(CancellationToken ct)
    {
        var (result, error) = await workers.SetAllWorkersMonitoringAsync(false, ct);
        if (error is not null || result is null)
        {
            return BadRequest(new { error = error ?? "Не удалось остановить мониторинг." });
        }

        return Ok(new
        {
            message = result.UpdatedCount > 0
                ? $"Мониторинг остановлен на {result.UpdatedCount} воркерах."
                : "Все воркеры уже были приостановлены.",
            result
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableWorker(Guid workerId, CancellationToken ct)
    {
        var (success, error) = await workers.SetWorkerEnabledAsync(workerId, true, ct);
        return success
            ? Ok(new { message = "Воркер включён." })
            : BadRequest(new { error = error ?? "Не удалось включить воркер." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableWorker(Guid workerId, CancellationToken ct)
    {
        var (success, error) = await workers.SetWorkerEnabledAsync(workerId, false, ct);
        return success
            ? Ok(new { message = "Воркер приостановлен." })
            : BadRequest(new { error = error ?? "Не удалось приостановить воркер." });
    }
}
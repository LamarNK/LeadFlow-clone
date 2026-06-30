using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class DashboardController(
    IDashboardService dashboard,
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
            Charts = model.Charts
        });
    }
}
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Helpers;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize(Policy = PanelPermissions.Statistics)]
public sealed class StatisticsController(
    IStatisticsService statistics,
    AuthSession session,
    OrbitaAuthService auth) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        string? from,
        string? to,
        Guid[]? workerIds,
        Guid[]? accountIds,
        string? vacancy,
        CancellationToken ct)
    {
        var period = DashboardPeriod.Parse(from, to, BrowserTimeZone.Resolve(HttpContext));
        var model = await statistics.GetIndexAsync(
            period,
            StatisticsService.NormalizeIds(workerIds),
            StatisticsService.NormalizeIds(accountIds),
            vacancy,
            ct);
        if (!string.IsNullOrWhiteSpace(model.ErrorMessage) && IsApiSessionMissing())
        {
            await auth.SignOutAsync(ct);
            return RedirectToAction("Login", "Account");
        }

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Snapshot(
        string? from,
        string? to,
        Guid[]? workerIds,
        Guid[]? accountIds,
        string? vacancy,
        CancellationToken ct)
    {
        var period = DashboardPeriod.Parse(from, to, BrowserTimeZone.Resolve(HttpContext));
        var model = await statistics.GetIndexAsync(
            period,
            StatisticsService.NormalizeIds(workerIds),
            StatisticsService.NormalizeIds(accountIds),
            vacancy,
            ct);
        if (!string.IsNullOrWhiteSpace(model.ErrorMessage))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = model.ErrorMessage });
        }

        return Json(new StatisticsLiveSnapshotViewModel
        {
            UpdatedAtUtc = model.Header.UpdatedAtUtc,
            KpiCards = model.KpiCards,
            BalanceRows = model.BalanceRows,
            Charts = model.Charts,
            AccountStats = model.AccountStats,
            Workers = model.Workers,
            BitrixDeliveries = model.BitrixDeliveries,
            CrmDeliveries = model.CrmDeliveries,
            HrInsights = model.HrInsights,
            Summary = model.Summary
        });
    }

    private bool IsApiSessionMissing() =>
        string.IsNullOrWhiteSpace(session.Token)
        || session.Token.Count(c => c == '.') < 2;
}

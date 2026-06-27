using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class MySettingsController(IMySettingsService settings) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? tab, CancellationToken ct = default)
    {
        var model = await settings.GetIndexAsync(tab, ct);
        model = model with
        {
            StatusMessage = TempData["MySettingsStatus"] as string ?? model.StatusMessage,
            ErrorMessage = TempData["MySettingsError"] as string ?? model.ErrorMessage
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeOwnPassword(ChangeOwnPasswordFormModel model, CancellationToken ct = default)
    {
        if (!string.Equals(model.NewPassword, model.ConfirmPassword, StringComparison.Ordinal))
        {
            TempData["MySettingsError"] = "Новый пароль и подтверждение не совпадают.";
            return RedirectToAction(nameof(Index), new { tab = "profile" });
        }

        var (success, error) = await settings.ChangeOwnPasswordAsync(model.CurrentPassword, model.NewPassword, ct);
        TempData[success ? "MySettingsStatus" : "MySettingsError"] = success
            ? "Пароль изменён."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "profile" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBitrix(SaveBitrixIntegrationFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.SaveBitrixAsync(model.WebhookUrl, ct);
        TempData[success ? "MySettingsStatus" : "MySettingsError"] = success
            ? "Вебхук сохранён и проверен."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "bitrix" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ValidateBitrix(
        string? webhookUrl,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        webhookUrl = string.IsNullOrWhiteSpace(webhookUrl) ? null : webhookUrl.Trim();
        var (validation, error) = await api.ValidateMyBitrixIntegrationAsync(webhookUrl, ct);
        if (validation is null)
        {
            return BadRequest(new { error });
        }

        return Json(validation);
    }
}
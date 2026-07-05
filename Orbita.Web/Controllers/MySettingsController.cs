using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class MySettingsController(
    IMySettingsService settings,
    BitrixValidationResultCache bitrixValidationCache) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? tab, CancellationToken ct = default)
    {
        var model = await settings.GetIndexAsync(tab, ct);
        var bitrix = model.Bitrix;
        if (model.ActiveTab == "bitrix"
            && bitrix is not null
            && TempData["BitrixValidationCacheKey"] is string cacheKey)
        {
            var cached = bitrixValidationCache.Take(cacheKey);
            if (cached is not null)
            {
                bitrix = bitrix.WithLiveValidation(cached.DraftWebhookUrl, cached.Validation);
            }
        }

        model = model with
        {
            Bitrix = bitrix,
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
    public async Task<IActionResult> SaveBitrixTransmission(SaveBitrixTransmissionFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.SaveBitrixTransmissionAsync(model.TransmissionEnabled, ct);
        TempData[success ? "MySettingsStatus" : "MySettingsError"] = success
            ? model.TransmissionEnabled
                ? "Передача в Bitrix24 включена."
                : "Передача в Bitrix24 отключена."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "bitrix" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ValidateBitrix(
        SaveBitrixIntegrationFormModel model,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        var draftWebhookUrl = model.WebhookUrl ?? string.Empty;
        var webhookUrl = string.IsNullOrWhiteSpace(model.WebhookUrl) ? null : model.WebhookUrl.Trim();

        var (validation, error) = await api.ValidateOfficeBitrixIntegrationAsync(webhookUrl, ct);
        TempData["BitrixValidationCacheKey"] = bitrixValidationCache.Store(draftWebhookUrl, validation);
        if (validation is null)
        {
            TempData["MySettingsError"] = error ?? "Не удалось выполнить проверку вебхука.";
        }

        return RedirectToAction(nameof(Index), new { tab = "bitrix" });
    }
}
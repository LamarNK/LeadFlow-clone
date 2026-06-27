using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Authorization;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize(Roles = OrbitaRoles.Admin)]
public sealed class SettingsController(
    ISettingsService settings,
    ThemeService theme) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        string? tab,
        string? q,
        string? level,
        string? service,
        string? action,
        string? userId,
        DateTime? date,
        int page = 1,
        CancellationToken ct = default)
    {
        var model = await settings.GetIndexAsync(tab, q, level, service, date, action, userId, page, ct);
        model = model with
        {
            StatusMessage = TempData["SettingsStatus"] as string ?? model.StatusMessage,
            ErrorMessage = TempData["SettingsError"] as string ?? model.ErrorMessage
        };
        ViewBag.RotatedWorkerApiKey = TempData["RotatedWorkerApiKey"] as string;
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUser(CreatePanelUserFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.CreateUserAsync(model.Email, model.Password, model.Role, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Пользователь добавлен."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "users" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteUser(string userId, CancellationToken ct = default)
    {
        var (success, error) = await settings.DeleteUserAsync(userId, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Пользователь удалён."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "users" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPanelUserPasswordFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.ResetUserPasswordAsync(model.UserId, model.Password, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Пароль обновлён."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "users" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateUserRole(UpdatePanelUserRoleFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.UpdateUserRoleAsync(model.UserId, model.Role, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Профиль пользователя обновлён."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "users" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LockUser(string userId, CancellationToken ct = default)
    {
        var (success, error) = await settings.LockUserAsync(userId, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Пользователь заблокирован."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "users" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlockUser(string userId, CancellationToken ct = default)
    {
        var (success, error) = await settings.UnlockUserAsync(userId, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Пользователь разблокирован."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "users" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeUserSessions(string userId, CancellationToken ct = default)
    {
        var (success, error) = await settings.RevokeUserSessionsAsync(userId, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Сессии пользователя завершены."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "users" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RenameWorker(RenameAdminWorkerFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.RenameWorkerAsync(model.WorkerId, model.DisplayName, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Имя воркера обновлено."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "workers" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableWorker(Guid workerId, CancellationToken ct = default)
    {
        var (success, error) = await settings.SetWorkerEnabledAsync(workerId, true, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Воркер включён."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "workers" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableWorker(Guid workerId, CancellationToken ct = default)
    {
        var (success, error) = await settings.SetWorkerEnabledAsync(workerId, false, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Воркер отключён."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "workers" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateWorkerKey(Guid workerId, CancellationToken ct = default)
    {
        var (apiKey, error) = await settings.RotateWorkerApiKeyAsync(workerId, ct);
        if (apiKey is null)
        {
            TempData["SettingsError"] = error;
            return RedirectToAction(nameof(Index), new { tab = "workers" });
        }

        TempData["SettingsStatus"] = "API-ключ перевыпущен. Скопируйте его сейчас — повторно он не будет показан.";
        TempData["RotatedWorkerApiKey"] = apiKey;
        return RedirectToAction(nameof(Index), new { tab = "workers" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveUserBitrix(SaveAdminBitrixIntegrationFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.SaveUserBitrixAsync(model.UserId, model.WebhookUrl, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Вебхук пользователя сохранён и проверен."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "integrations", userId = model.UserId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ValidateUserBitrix(
        string userId,
        string? webhookUrl,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        webhookUrl = string.IsNullOrWhiteSpace(webhookUrl) ? null : webhookUrl.Trim();
        var (validation, error) = await api.ValidateAdminUserBitrixIntegrationAsync(userId, webhookUrl, ct);
        if (validation is null)
        {
            return BadRequest(new { error });
        }

        return Json(validation);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [AllowAnonymous]
    public IActionResult ToggleTheme()
    {
        theme.Toggle();
        Response.Cookies.Append(ThemeService.CookieName, theme.Theme, new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });
        var referer = Request.Headers.Referer.ToString();
        return Redirect(string.IsNullOrWhiteSpace(referer) ? "/" : referer);
    }
}
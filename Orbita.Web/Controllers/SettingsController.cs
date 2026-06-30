using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
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
        Guid? workerId,
        DateTime? date,
        int page = 1,
        CancellationToken ct = default)
    {
        var model = await settings.GetIndexAsync(tab, q, level, service, date, action, userId, workerId, page, ct);
        model = model with
        {
            StatusMessage = TempData["SettingsStatus"] as string ?? model.StatusMessage,
            ErrorMessage = TempData["SettingsError"] as string ?? model.ErrorMessage
        };
        ViewBag.RotatedWorkerApiKey = TempData["RotatedWorkerApiKey"] as string;
        ViewBag.RotatedOfficeRegistrationSecret = TempData["RotatedOfficeRegistrationSecret"] as string;
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUser(CreatePanelUserFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.CreateUserAsync(model.Email, model.Password, model.Role, model.OfficeId, ct);
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
    public async Task<IActionResult> UpdateUserOffice(UpdatePanelUserOfficeFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.UpdateUserOfficeAsync(model.UserId, model.OfficeId, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Офис пользователя обновлён."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "users" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateOffice(CreateOfficeFormModel model, CancellationToken ct = default)
    {
        var (success, error, _) = await settings.CreateOfficeAsync(model.Name, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Офис создан. Секрет регистрации сгенерирован — перевыпустите его в карточке офиса при необходимости."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "offices" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateOffice(UpdateOfficeFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.UpdateOfficeAsync(
            model.OfficeId,
            model.Name,
            model.IsEnabled,
            model.BitrixTransmissionEnabled,
            ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Офис обновлён."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "offices", userId = model.OfficeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateOfficeRegistrationSecret(Guid officeId, CancellationToken ct = default)
    {
        var (success, error, secret) = await settings.RotateOfficeRegistrationSecretAsync(officeId, ct);
        if (!success || secret is null)
        {
            TempData["SettingsError"] = error;
            return RedirectToAction(nameof(Index), new { tab = "offices", userId = officeId });
        }

        TempData["SettingsStatus"] = "Секрет регистрации перевыпущен. Скопируйте его сейчас — повторно он не будет показан.";
        TempData["RotatedOfficeRegistrationSecret"] = secret;
        return RedirectToAction(nameof(Index), new { tab = "offices", userId = officeId });
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
    [RequestSizeLimit(536_870_912)]
    [RequestFormLimits(MultipartBodyLengthLimit = 536_870_912)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadWorkerRelease(
        IFormFile? packageFile,
        string? version,
        string? releaseNotes,
        CancellationToken ct = default)
    {
        if (packageFile is null || packageFile.Length == 0)
        {
            TempData["SettingsError"] = ModelState.ErrorCount > 0
                ? "Не удалось принять файл. Проверьте размер MSI (до 512 МБ)."
                : "Выберите MSI-файл.";
            return RedirectToAction(nameof(Index), new { tab = "worker-releases" });
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            TempData["SettingsError"] = "Укажите версию или используйте имя Orbita.Worker.Setup-1.0.0.1.msi.";
            return RedirectToAction(nameof(Index), new { tab = "worker-releases" });
        }

        var (success, error) = await settings.UploadWorkerReleaseAsync(
            packageFile,
            version,
            releaseNotes,
            ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Релиз воркера загружен."
            : error ?? "Не удалось загрузить релиз.";
        return RedirectToAction(nameof(Index), new { tab = "worker-releases" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetWorkerReleaseLatest(string version, CancellationToken ct = default)
    {
        var (success, error) = await settings.SetWorkerReleaseLatestAsync(version, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? $"Версия {version} назначена актуальной."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "worker-releases" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteWorkerRelease(string version, CancellationToken ct = default)
    {
        var (success, error) = await settings.DeleteWorkerReleaseAsync(version, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? $"Версия {version} удалена."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "worker-releases" });
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
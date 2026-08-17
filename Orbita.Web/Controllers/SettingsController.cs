using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Authorization;
using Orbita.Web.Helpers;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize(Policy = PanelPermissions.Administration)]
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
        string? userId,
        Guid? officeId,
        Guid? instanceId,
        Guid? workerId,
        DateTime? date,
        int page = 1,
        CancellationToken ct = default)
    {
        var model = await settings.GetIndexAsync(tab, q, level, service, date, userId, officeId, instanceId, workerId, page, ct);
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
        var (success, error) = await settings.CreateUserAsync(model.Email, model.FullName, model.Password, model.Role, model.OfficeId, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Пользователь добавлен."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "users" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateUserFullName(UpdatePanelUserFullNameFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.UpdateUserFullNameAsync(model.UserId, model.FullName, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "ФИО пользователя обновлено."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "users" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateUser(UpdatePanelUserFormModel model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model.FullName))
        {
            TempData["SettingsError"] = "Укажите ФИО пользователя.";
            return RedirectToAction(nameof(Index), new { tab = "users" });
        }

        var currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var canEditAccess = !string.Equals(model.UserId, currentUserId, StringComparison.Ordinal);
        var targetRole = PanelRoles.Normalize(canEditAccess ? model.Role : model.OriginalRole);
        var permissions = PanelPermissions.Normalize(model.Permissions);
        var originalPermissions = PanelPermissions.Normalize(
            (model.OriginalPermissionKeys ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var permissionsChanged = model.UseProfilePermissions != model.OriginalUseProfilePermissions
                                 || (!model.UseProfilePermissions && !permissions.SequenceEqual(originalPermissions));
        if (canEditAccess
            && !model.UseProfilePermissions
            && targetRole == PanelRoles.Admin
            && !permissions.Contains(PanelPermissions.Administration, StringComparer.Ordinal))
        {
            TempData["SettingsError"] = "У администратора должно остаться право «Администрирование».";
            return RedirectToAction(nameof(Index), new { tab = "users" });
        }

        var changes = new List<Func<Task<(bool Success, string? Error)>>>();

        if (canEditAccess && !string.Equals(model.Role, model.OriginalRole, StringComparison.Ordinal))
        {
            changes.Add(() => settings.UpdateUserRoleAsync(model.UserId, model.Role ?? string.Empty, ct));
        }

        if (canEditAccess
            && !string.Equals(model.Role, PanelRoles.Admin, StringComparison.Ordinal)
            && model.OfficeId != model.OriginalOfficeId)
        {
            changes.Add(() => settings.UpdateUserOfficeAsync(model.UserId, model.OfficeId, ct));
        }

        if (canEditAccess && permissionsChanged)
        {
            changes.Add(() => settings.UpdateUserPermissionsAsync(
                model.UserId,
                model.UseProfilePermissions,
                permissions,
                ct));
        }

        if (!string.Equals(model.FullName.Trim(), model.OriginalFullName.Trim(), StringComparison.Ordinal))
        {
            changes.Add(() => settings.UpdateUserFullNameAsync(model.UserId, model.FullName, ct));
        }

        if (canEditAccess && !string.IsNullOrWhiteSpace(model.Password))
        {
            changes.Add(() => settings.ResetUserPasswordAsync(model.UserId, model.Password, ct));
        }

        foreach (var change in changes)
        {
            var (success, error) = await change();
            if (!success)
            {
                TempData["SettingsError"] = error;
                return RedirectToAction(nameof(Index), new { tab = "users" });
            }
        }

        TempData["SettingsStatus"] = changes.Count == 0
            ? "Изменений нет."
            : "Пользователь обновлён.";
        return RedirectToAction(nameof(Index), new { tab = "users" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAccessProfile(UpdateAccessProfileFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.UpdateAccessProfileAsync(model.ProfileId, model.Permissions, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Права доступа обновлены. Пользователям этого профиля нужно войти заново."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "profiles" });
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
        var (success, error, officeId, _) = await settings.CreateOfficeAsync(model.Name, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Офис создан. Секрет регистрации сгенерирован — перевыпустите его в карточке офиса при необходимости."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "offices", officeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateOffice(UpdateOfficeFormModel model, CancellationToken ct = default)
    {
        var (success, error) = await settings.UpdateOfficeAsync(
            model.OfficeId,
            model.Name,
            model.IsEnabled,
            model.CrmEnabled,
            ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Офис обновлён."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "offices", officeId = model.OfficeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateOfficeRegistrationSecret(Guid officeId, CancellationToken ct = default)
    {
        var (success, error, secret) = await settings.RotateOfficeRegistrationSecretAsync(officeId, ct);
        if (!success || secret is null)
        {
            TempData["SettingsError"] = error;
            return RedirectToAction(nameof(Index), new { tab = "offices", officeId });
        }

        TempData["SettingsStatus"] = "Секрет регистрации перевыпущен. Скопируйте его сейчас — повторно он не будет показан.";
        TempData["RotatedOfficeRegistrationSecret"] = secret;
        return RedirectToAction(nameof(Index), new { tab = "offices", officeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOffice(Guid officeId, CancellationToken ct = default)
    {
        var (success, error) = await settings.DeleteOfficeAsync(officeId, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Офис удалён."
            : error;
        return success
            ? RedirectToAction(nameof(Index), new { tab = "offices" })
            : RedirectToAction(nameof(Index), new { tab = "offices", officeId });
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
    public Task<IActionResult> SaveOfficeBitrix(SaveOfficeBitrixIntegrationFormModel model, CancellationToken ct = default)
    {
        TempData["SettingsError"] = "Настройка вебхука перенесена на вкладку «Битриксы и связи». Добавьте или обновите Битрикс в реестре интеграций.";
        return Task.FromResult<IActionResult>(RedirectToAction(nameof(Index), new { tab = "bitrix", officeId = model.OfficeId }));
    }

    [HttpGet]
    public IActionResult CreateBitrixInstance(Guid officeId, string? tab = "integrations") =>
        RedirectToAction(nameof(Index), new { tab, officeId, instanceId = Guid.Empty });

    [HttpGet]
    public IActionResult EditBitrixInstance(Guid officeId, Guid id, string? tab = "integrations") =>
        RedirectToAction(nameof(Index), new { tab, officeId, instanceId = id });

    [HttpGet]
    public async Task<IActionResult> Telephony(
        Guid officeId,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        var settings = await api.GetCrmTelephonySettingsAsync(officeId, ct);
        var offices = await api.GetOfficesAsync(ct) ?? [];
        var office = offices.FirstOrDefault(x => x.Id == officeId);
        if (settings is null || office is null)
        {
            return NotFound();
        }

        var users = (await api.GetPanelUsersAsync(ct) ?? [])
            .Where(x => x.OfficeId == officeId)
            .OrderBy(x => string.IsNullOrWhiteSpace(x.FullName) ? x.Email : x.FullName)
            .ToList();
        return View(new CrmTelephonyPageViewModel
        {
            OfficeId = officeId,
            OfficeName = office.Name,
            Settings = settings,
            OfficeUsers = users,
            SipoutWebRequestUrl = TempData["SipoutWebRequestUrl"] as string,
            StatusMessage = TempData["SettingsStatus"] as string,
            ErrorMessage = TempData["SettingsError"] as string
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateTelephonyReceiver(
        Guid officeId,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        var (receiver, error) = await api.RotateCrmTelephonyReceiverAsync(officeId, ct);
        if (receiver is null)
        {
            TempData["SettingsError"] = error;
        }
        else
        {
            TempData["SettingsStatus"] = "Новый защищённый адрес SIPOUT создан. Скопируйте его сейчас: секрет повторно не показывается.";
            TempData["SipoutWebRequestUrl"] = receiver.SipoutWebRequestUrl;
        }
        return RedirectToAction(nameof(Telephony), new { officeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetTelephonyEnabled(
        Guid officeId,
        bool enabled,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        var (success, error) = await api.SetCrmTelephonyEnabledAsync(officeId, enabled, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? enabled ? "Приём звонков SIPOUT включён." : "Приём звонков SIPOUT приостановлен."
            : error;
        return RedirectToAction(nameof(Telephony), new { officeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTelephonyBinding(
        SaveCrmTelephonyBindingFormModel model,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        var (binding, error) = await api.SetCrmTelephonyBindingAsync(
            model.OfficeId, model.UserId, model.ProviderUserKey, ct);
        TempData[binding is null ? "SettingsError" : "SettingsStatus"] = binding is null
            ? error
            : $"SIPOUT {binding.ProviderUserKey} привязан к сотруднику {binding.UserName}.";
        return RedirectToAction(nameof(Telephony), new { officeId = model.OfficeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTelephonyBinding(
        Guid officeId,
        string userId,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        var (success, error) = await api.RemoveCrmTelephonyBindingAsync(officeId, userId, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Привязка SIPOUT удалена."
            : error;
        return RedirectToAction(nameof(Telephony), new { officeId });
    }

    [HttpGet]
    public async Task<IActionResult> BitrixCrmImport(
        Guid officeId,
        Guid instanceId,
        int categoryId = 0,
        [FromServices] OrbitaApiClient api = null!,
        CancellationToken ct = default)
    {
        var instance = await api.GetBitrixInstanceAsync(instanceId, officeId, ct);
        if (instance is null)
        {
            return NotFound();
        }

        return View(new BitrixCrmImportPageViewModel
        {
            OfficeId = officeId,
            BitrixInstanceId = instanceId,
            BitrixInstanceLabel = string.IsNullOrWhiteSpace(instance.Signature)
                ? instance.Name
                : $"{instance.Name} · {instance.Signature}",
            PortalHost = instance.PortalHost ?? string.Empty,
            CategoryId = Math.Max(0, categoryId)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewBitrixCrmImport(
        Guid officeId,
        Guid bitrixInstanceId,
        int categoryId,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        var instance = await api.GetBitrixInstanceAsync(bitrixInstanceId, officeId, ct);
        if (instance is null)
        {
            return NotFound();
        }

        var (preview, error) = await api.PreviewBitrixCrmImportAsync(
            bitrixInstanceId,
            new BitrixCrmImportPreviewRequest(Math.Max(0, categoryId), BitrixCrmImportStages.Default),
            officeId,
            ct);
        return View("BitrixCrmImport", BuildBitrixCrmImportPage(instance, Math.Max(0, categoryId), preview, null, null, error));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExecuteBitrixCrmImport(
        ExecuteBitrixCrmImportFormModel model,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        var instance = await api.GetBitrixInstanceAsync(model.BitrixInstanceId, model.OfficeId, ct);
        if (instance is null)
        {
            return NotFound();
        }

        if (!model.Confirmed || model.DealIds.Count == 0)
        {
            var (preview, previewError) = await api.PreviewBitrixCrmImportAsync(
                model.BitrixInstanceId,
                new BitrixCrmImportPreviewRequest(model.CategoryId, BitrixCrmImportStages.Default),
                model.OfficeId,
                ct);
            var error = previewError ?? "Выберите хотя бы одну карточку и подтвердите импорт.";
            return View("BitrixCrmImport", BuildBitrixCrmImportPage(instance, model.CategoryId, preview, null, null, error));
        }

        var (result, importError) = await api.ExecuteBitrixCrmImportAsync(
            model.BitrixInstanceId,
            new BitrixCrmImportExecuteRequest(model.CategoryId, BitrixCrmImportStages.Default, model.DealIds),
            model.OfficeId,
            ct);
        var status = result is null
            ? null
            : $"Импорт завершён: создано {result.Created}, обновлено {result.Updated}, уже было {result.AlreadyImported}, пропущено {result.Skipped}.";
        return View("BitrixCrmImport", BuildBitrixCrmImportPage(
            instance,
            model.CategoryId,
            null,
            result,
            status,
            importError));
    }

    private static BitrixCrmImportPageViewModel BuildBitrixCrmImportPage(
        BitrixInstanceDto instance,
        int categoryId,
        BitrixCrmImportPreviewDto? preview,
        BitrixCrmImportResultDto? result,
        string? status,
        string? error) => new()
    {
        OfficeId = instance.OfficeId,
        BitrixInstanceId = instance.Id,
        BitrixInstanceLabel = string.IsNullOrWhiteSpace(instance.Signature)
            ? instance.Name
            : $"{instance.Name} · {instance.Signature}",
        PortalHost = instance.PortalHost ?? string.Empty,
        CategoryId = categoryId,
        Preview = preview,
        Result = result,
        StatusMessage = status,
        ErrorMessage = error
    };

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBitrixInstance(
        SaveBitrixInstanceFormModel model,
        Guid officeId,
        CancellationToken ct = default)
    {
        var (success, error, instanceId) = await settings.SaveBitrixInstanceAsync(model, officeId, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? model.Id is Guid existingId && existingId != Guid.Empty
                ? "Битрикс обновлён."
                : "Битрикс создан."
            : error;
        return RedirectToAction(nameof(Index), new
        {
            tab = "integrations",
            officeId,
            instanceId = success ? instanceId : model.Id
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBitrixInstance(Guid id, Guid officeId, CancellationToken ct = default)
    {
        var (success, error) = await settings.DeleteBitrixInstanceAsync(id, officeId, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Битрикс удалён."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "integrations", officeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBitrixWorkforce(
        SaveBitrixWorkforceFormModel model,
        CancellationToken ct = default)
    {
        var (success, error) = await settings.SaveBitrixWorkforceAsync(model, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? model.OperationMode == BitrixWorkforceDistribution.WriterMode
                ? "Распределение сохранено в режиме writer."
                : model.OperationMode == BitrixWorkforceDistribution.ShadowMode
                    ? "Распределение сохранено в безопасном shadow-режиме."
                    : "Распределение отключено."
            : error;
        return RedirectToAction(nameof(Index), new
        {
            tab = "integrations",
            officeId = model.OfficeId,
            instanceId = model.BitrixInstanceId
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfigureBitrixWorkforceReceiver(
        ConfigureBitrixWorkforceReceiverFormModel model,
        CancellationToken ct = default)
    {
        var (success, error) = await settings.ConfigureBitrixWorkforceReceiverAsync(model, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Приём событий Bitrix24 настроен. Добавьте показанный endpoint в исходящий вебхук."
            : error;
        return RedirectToAction(nameof(Index), new
        {
            tab = "integrations",
            officeId = model.OfficeId,
            instanceId = model.BitrixInstanceId
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ValidateAdminBitrixInstance(
        ValidateBitrixInstanceFormModel model,
        Guid officeId,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        var webhookUrl = string.IsNullOrWhiteSpace(model.WebhookUrl) ? null : model.WebhookUrl.Trim();
        var (validation, error) = model.Id != Guid.Empty
            ? await api.ValidateBitrixInstanceAsync(model.Id, webhookUrl, officeId, ct)
            : await api.ValidateBitrixWebhookAsync(webhookUrl, officeId, ct);
        if (validation is null)
        {
            return BadRequest(new { error = error ?? "Не удалось выполнить проверку вебхука." });
        }

        return Json(validation);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBitrixTransmission(
        SaveBitrixTransmissionFormModel model,
        Guid officeId,
        CancellationToken ct = default)
    {
        var transmissionEnabled = FormBindingHelper.ReadCheckbox(Request.Form, "TransmissionEnabled");
        var (success, error) = await settings.SaveBitrixTransmissionAsync(officeId, transmissionEnabled, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? transmissionEnabled
                ? "Автораспределение откликов включено."
                : "Автораспределение откликов отключено."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "integrations", officeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAdminDistributionRoute(
        SaveDistributionRouteFormModel model,
        Guid officeId,
        CancellationToken ct = default)
    {
        IReadOnlyList<SaveDistributionNodeRequest> nodes;
        try
        {
            nodes = System.Text.Json.JsonSerializer.Deserialize<List<SaveDistributionNodeRequest>>(model.NodesJson)
                ?? [];
        }
        catch
        {
            TempData["SettingsError"] = "Некорректная схема связей.";
            return RedirectToAction(nameof(Index), new { tab = "integrations", officeId });
        }

        var autoEnabled = FormBindingHelper.ReadCheckbox(Request.Form, "IsAutoDistributionEnabled");
        var bitrixLeadQuotas = FormBindingHelper.ParseBitrixLeadQuotas(model.BitrixQuotasJson);
        var (success, error) = await settings.SaveDistributionRouteAsync(
            autoEnabled,
            nodes,
            officeId,
            bitrixLeadQuotas,
            ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Схема связей сохранена."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "integrations", officeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ValidateOfficeBitrix(
        Guid officeId,
        string? webhookUrl,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        webhookUrl = string.IsNullOrWhiteSpace(webhookUrl) ? null : webhookUrl.Trim();
        var (validation, error) = await api.ValidateAdminOfficeBitrixIntegrationAsync(officeId, webhookUrl, ct);
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

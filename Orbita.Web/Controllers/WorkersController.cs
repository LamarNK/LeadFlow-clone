using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Helpers;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;
using System.Linq;

namespace Orbita.Web.Controllers;

[Authorize(Policy = PanelPermissions.Workers)]
public sealed class WorkersController(IWorkersService workers) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Snapshot(string? q, string? status, int page = 1, int? pageSize = null, string? sort = null, string? dir = null, CancellationToken ct = default)
    {
        var model = await workers.GetIndexAsync(q, status, page, pageSize, sort, dir, ct);
        return Json(new WorkersLiveSnapshotViewModel
        {
            UpdatedAtUtc = model.Header.UpdatedAtUtc,
            KpiCards = model.KpiCards,
            Workers = model.Workers,
            Pagination = model.Pagination
        });
    }

    [HttpGet]
    public async Task<IActionResult> DetailsSnapshot(
        Guid id,
        string? sort = null,
        string? dir = null,
        string? q = null,
        string? groupId = null,
        string? provider = null,
        CancellationToken ct = default)
    {
        provider = string.IsNullOrWhiteSpace(provider)
            ? WorkerAccountCatalogFilter.AdsPowerProvider
            : provider;
        var model = await workers.GetDetailsAsync(
            id,
            sort: sort,
            sortDir: dir,
            accountSearchQuery: q,
            accountGroupId: groupId,
            accountProvider: provider,
            ct: ct);

        if (model is null)
        {
            return NotFound();
        }

        return Json(new WorkerDetailsLiveSnapshotViewModel
        {
            UpdatedAtUtc = model.UpdatedAtUtc,
            IsOnline = model.IsOnline,
            IsEnabled = model.IsEnabled,
            IsMonitoringPaused = model.IsMonitoringPaused,
            LastActivityUtc = model.LastActivityUtc,
            CpuPercent = model.System.CpuPercent,
            RamPercent = model.System.RamPercent,
            RamUsedMb = model.System.RamUsedMb,
            RamTotalMb = model.System.RamTotalMb,
            KpiCards = model.KpiCards,
            InfoItems = model.InfoItems,
            PeriodStats = model.PeriodStats,
            MonitoringCycles = model.MonitoringCycles,
            Accounts = model.Accounts,
            Events = model.Events,
            ActivityChart = model.ActivityChart,
            CurrentActivity = model.CurrentActivity,
            ActiveAccountActivities = model.ActiveAccountActivities,
            AdsPowerGroups = model.AdsPowerGroups,
            AccountGroupOptions = model.AccountGroupOptions,
            AdsPowerAccountCount = model.AdsPowerAccountCount,
            MultiloginAccountCount = model.MultiloginAccountCount,
            LocalAccountCount = model.LocalAccountCount,
            CatalogAccountCount = model.CatalogAccountCount,
            AdsPowerCheck = model.AdsPowerCheck,
            MultiloginCheck = model.MultiloginCheck,
            LocalChromeCheck = model.LocalChromeCheck
        });
    }

    [HttpGet]
    public async Task<IActionResult> MonitoringCycles(Guid id, CancellationToken ct = default)
    {
        var model = await workers.GetDetailsAsync(id, ct: ct);
        return model is null
            ? NotFound()
            : PartialView("~/Views/Statistics/_MonitoringCycleAccounts.cshtml", model.MonitoringCycles);
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? q, string? status, int page = 1, int? pageSize = null, string? sort = null, string? dir = null, CancellationToken ct = default)
    {
        var model = await workers.GetIndexAsync(q, status, page, pageSize, sort, dir, ct);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadLatest(CancellationToken ct)
    {
        var (stream, fileName, error) = await workers.OpenLatestWorkerReleaseDownloadAsync(ct);
        if (stream is null || fileName is null)
        {
            TempData["WorkersError"] = error ?? "Релиз воркера не найден.";
            return RedirectToAction(nameof(Index));
        }

        return File(stream, "application/octet-stream", fileName);
    }

    [HttpGet]
    public async Task<IActionResult> Browsers(Guid id, CancellationToken ct = default)
    {
        var model = await workers.GetDetailsAsync(id, ct: ct);
        if (model is null)
        {
            return NotFound();
        }

        return View(new WorkerBrowserMonitorViewModel
        {
            WorkerId = model.WorkerId,
            WorkerName = model.DisplayName,
            IsOnline = model.IsOnline,
            IsEnabled = model.IsEnabled,
            IsMonitoringPaused = model.IsMonitoringPaused,
            Breadcrumbs =
            [
                new BreadcrumbItemViewModel { Label = "Воркеры", Url = Url.Action(nameof(Index))! },
                new BreadcrumbItemViewModel { Label = model.DisplayName, Url = Url.Action(nameof(Details), new { id })! },
                new BreadcrumbItemViewModel { Label = "Активные браузеры", IsActive = true }
            ]
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(
        Guid id,
        string? sort = null,
        string? dir = null,
        string? q = null,
        string? groupId = null,
        string? provider = null,
        CancellationToken ct = default)
    {
        provider = string.IsNullOrWhiteSpace(provider)
            ? WorkerAccountCatalogFilter.AdsPowerProvider
            : provider;
        var model = await workers.GetDetailsAsync(
            id,
            sort: sort,
            sortDir: dir,
            accountSearchQuery: q,
            accountGroupId: groupId,
            accountProvider: provider,
            ct);
        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string displayName, Guid? officeId, CancellationToken ct)
    {
        var (result, error) = await workers.CreateWorkerAsync(displayName, officeId, ct);
        if (error is not null || result is null)
        {
            TempData["WorkersError"] = error ?? "Не удалось создать воркер.";
            return RedirectToAction(nameof(Index));
        }

        TempData["CreatedWorkerApiKey"] = result.ApiKey;
        TempData["CreatedWorkerInstallCommand"] = result.InstallCommand;
        TempData["CreatedWorkerName"] = result.DisplayName;
        return RedirectToAction(nameof(Details), new { id = result.WorkerId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSettings(
        Guid workerId,
        int maxConcurrentAccounts,
        string? adsPowerApiBaseUrl,
        string? adsPowerApiKey,
        string? ruCaptchaApiKey,
        string? adsPowerGroupId,
        bool responseFilterEnabled = false,
        bool responseFilterExcludeFemale = false,
        bool responseFilterExcludeMale = false,
        int? responseFilterMaxAgeMale = null,
        int? responseFilterMaxAgeFemale = null,
        int? responseFilterMaxAgeDays = null,
        bool responseHighlightEnabled = false,
        string[]? responseHighlightAgeBuckets = null,
        string[]? responseHighlightTargets = null,
        bool autoScheduleEnabled = false,
        string[]? autoScheduleDays = null,
        string? autoScheduleFromLocalTime = null,
        string? autoScheduleToLocalTime = null,
        bool messengerAutoReplyEnabled = false,
        string? messengerAutoReplyMessage = null,
        int? phoneUnchangedHours = null,
        bool autoDeliverToCrm = false,
        bool autoDeliverToBitrix = false,
        string? settingsTab = null,
        string? multiloginLauncherUrl = null,
        string? multiloginCloudApiUrl = null,
        string? multiloginAutomationToken = null,
        string? localChromeExecutablePath = null,
        CancellationToken ct = default)
    {
        var responseHighlightAgeBucketsCsv = responseHighlightAgeBuckets is { Length: > 0 }
            ? string.Join(',', responseHighlightAgeBuckets.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
            : null;
        var autoScheduleDaysCsv = autoScheduleDays is { Length: > 0 }
            ? string.Join(',', autoScheduleDays.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
            : null;
        var responseHighlightTargetsJson = ResponseHighlightRules.NormalizeTargetsFormValues(responseHighlightTargets);

        // Unchecked checkboxes are omitted from form posts.
        autoDeliverToCrm = FormBindingHelper.ReadCheckbox(Request.Form, "autoDeliverToCrm");
        autoDeliverToBitrix = FormBindingHelper.ReadCheckbox(Request.Form, "autoDeliverToBitrix");
        var adsPowerEnabled = FormBindingHelper.ReadCheckbox(Request.Form, "adsPowerEnabled");
        var multiloginEnabled = FormBindingHelper.ReadCheckbox(Request.Form, "multiloginEnabled");
        var localChromeEnabled = FormBindingHelper.ReadCheckbox(Request.Form, "localChromeEnabled");

        var (success, error) = await workers.UpdateWorkerSettingsAsync(
            workerId,
            maxConcurrentAccounts,
            adsPowerApiBaseUrl,
            adsPowerApiKey,
            adsPowerGroupId,
            responseFilterEnabled,
            responseFilterExcludeFemale,
            responseFilterExcludeMale,
            responseFilterMaxAgeMale,
            responseFilterMaxAgeFemale,
            responseFilterMaxAgeDays,
            responseHighlightEnabled,
            responseHighlightAgeBucketsCsv,
            autoScheduleEnabled,
            autoScheduleDaysCsv,
            autoScheduleFromLocalTime,
            autoScheduleToLocalTime,
            messengerAutoReplyEnabled,
            messengerAutoReplyMessage,
            phoneUnchangedHours,
            autoDeliverToCrm,
            autoDeliverToBitrix,
            responseHighlightTargetsJson,
            ruCaptchaApiKey,
            multiloginLauncherUrl,
            multiloginCloudApiUrl,
            multiloginAutomationToken,
            localChromeExecutablePath,
            adsPowerEnabled,
            multiloginEnabled,
            localChromeEnabled,
            ct);
        if (!success)
        {
            TempData["WorkersError"] = error;
        }
        else
        {
            TempData["WorkersSuccess"] = "Настройки воркера сохранены.";
        }

        return RedirectToAction(nameof(Details), new { id = workerId, settingsTab });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSettingsTemplate(
        WorkerSettingsTemplateFormModel model,
        CancellationToken ct)
    {
        BindTemplateCheckboxes(model);
        var (template, templates, error) = await workers.CreateWorkerSettingsTemplateAsync(
            model.WorkerId,
            model.Name ?? string.Empty,
            model.ToPayload(),
            ct);
        if (error is not null || template is null)
        {
            return BadRequest(new { error = error ?? "Не удалось сохранить шаблон." });
        }

        return Ok(new { template, templates, message = "Шаблон сохранён." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSettingsTemplate(
        WorkerSettingsTemplateFormModel model,
        CancellationToken ct)
    {
        BindTemplateCheckboxes(model);
        if (model.TemplateId == Guid.Empty)
        {
            return BadRequest(new { error = "Выберите шаблон." });
        }

        var (template, templates, error) = await workers.UpdateWorkerSettingsTemplateAsync(
            model.WorkerId,
            model.TemplateId,
            model.Name ?? string.Empty,
            model.ToPayload(),
            ct);
        if (error is not null || template is null)
        {
            return StatusCode(
                error is not null && error.Contains("не найден", StringComparison.OrdinalIgnoreCase) ? 404 : 400,
                new { error = error ?? "Не удалось обновить шаблон." });
        }

        return Ok(new { template, templates, message = "Шаблон обновлён." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSettingsTemplate(
        Guid workerId,
        Guid templateId,
        CancellationToken ct)
    {
        var (templates, error) = await workers.DeleteWorkerSettingsTemplateAsync(workerId, templateId, ct);
        if (error is not null)
        {
            return StatusCode(
                error.Contains("не найден", StringComparison.OrdinalIgnoreCase) ? 404 : 400,
                new { error });
        }

        return Ok(new { templates, message = "Шаблон удалён." });
    }

    private void BindTemplateCheckboxes(WorkerSettingsTemplateFormModel model)
    {
        model.ResponseFilterExcludeFemale = FormBindingHelper.ReadCheckbox(Request.Form, nameof(model.ResponseFilterExcludeFemale));
        model.ResponseFilterExcludeMale = FormBindingHelper.ReadCheckbox(Request.Form, nameof(model.ResponseFilterExcludeMale));
        model.ResponseHighlightEnabled = FormBindingHelper.ReadCheckbox(Request.Form, nameof(model.ResponseHighlightEnabled));
        model.AutoScheduleEnabled = FormBindingHelper.ReadCheckbox(Request.Form, nameof(model.AutoScheduleEnabled));
        model.MessengerAutoReplyEnabled = FormBindingHelper.ReadCheckbox(Request.Form, nameof(model.MessengerAutoReplyEnabled));
        model.AutoDeliverToCrm = FormBindingHelper.ReadCheckbox(Request.Form, nameof(model.AutoDeliverToCrm));
        model.AutoDeliverToBitrix = FormBindingHelper.ReadCheckbox(Request.Form, nameof(model.AutoDeliverToBitrix));
        model.AdsPowerEnabled = FormBindingHelper.ReadCheckbox(Request.Form, nameof(model.AdsPowerEnabled));
        model.MultiloginEnabled = FormBindingHelper.ReadCheckbox(Request.Form, nameof(model.MultiloginEnabled));
        model.LocalChromeEnabled = FormBindingHelper.ReadCheckbox(Request.Form, nameof(model.LocalChromeEnabled));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enable(Guid workerId, string? returnTo, CancellationToken ct)
    {
        var (success, error) = await workers.SetWorkerEnabledAsync(workerId, true, ct);
        TempData[success ? "WorkersSuccess" : "WorkersError"] = success
            ? "Мониторинг возобновлён."
            : error;
        return RedirectAfterWorkerAction(workerId, returnTo);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disable(Guid workerId, string? returnTo, CancellationToken ct)
    {
        var (success, error) = await workers.SetWorkerEnabledAsync(workerId, false, ct);
        TempData[success ? "WorkersSuccess" : "WorkersError"] = success
            ? "Мониторинг поставлен на паузу."
            : error;
        return RedirectAfterWorkerAction(workerId, returnTo);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rename(
        Guid workerId,
        string displayName,
        string? q,
        string? sort,
        string? dir,
        string? provider,
        string? groupId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            TempData["WorkersError"] = "Укажите имя воркера.";
        }
        else
        {
            var (success, error) = await workers.RenameWorkerAsync(workerId, displayName.Trim(), ct);
            TempData[success ? "WorkersSuccess" : "WorkersError"] = success
                ? "Имя воркера обновлено."
                : (error ?? "Не удалось переименовать воркер.");
        }

        return RedirectToAction(nameof(Details), new
        {
            id = workerId,
            q,
            sort,
            dir,
            provider,
            groupId
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid workerId, CancellationToken ct)
    {
        var (success, error) = await workers.DeleteWorkerAsync(workerId, ct);
        if (!success)
        {
            TempData["WorkersError"] = error;
            return RedirectToAction(nameof(Details), new { id = workerId });
        }

        TempData["WorkersSuccess"] = "Воркер удалён.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateKey(Guid workerId, CancellationToken ct)
    {
        var (apiKey, error) = await workers.RotateWorkerApiKeyAsync(workerId, ct);
        if (apiKey is null)
        {
            TempData["WorkersError"] = error;
            return RedirectToAction(nameof(Details), new { id = workerId });
        }

        TempData["CreatedWorkerApiKey"] = apiKey;
        TempData["WorkersSuccess"] = "API-ключ перевыпущен. Скопируйте его сейчас — повторно он не будет показан.";
        return RedirectToAction(nameof(Details), new { id = workerId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSubProfile(
        Guid workerId,
        Guid accountId,
        string subProfileId,
        bool isEnabledInPanel,
        CancellationToken ct)
    {
        var (success, error) = await workers.UpdateSubProfileEnabledAsync(
            workerId, accountId, subProfileId, isEnabledInPanel, ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось сохранить." });
        }

        return Ok(new
        {
            message = isEnabledInPanel ? "Субпрофиль включён." : "Субпрофиль отключён.",
            isEnabledInPanel
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshSubProfiles(Guid workerId, Guid accountId, CancellationToken ct)
    {
        var (success, error) = await workers.RequestSubProfilesRefreshAsync(workerId, accountId, ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось отправить запрос." });
        }

        return Ok(new { message = "Воркер обновит субпрофили при следующем цикле." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckProvider(Guid workerId, string provider, CancellationToken ct)
    {
        var (success, error) = await workers.RequestProviderCheckAsync(workerId, provider, ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось отправить запрос." });
        }

        return Ok(new { message = WorkerBrowserProviderMessages.CheckQueued });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncProvider(Guid workerId, string provider, CancellationToken ct)
    {
        var (success, error) = await workers.RequestProviderSyncAsync(workerId, provider, ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось отправить запрос." });
        }

        return Ok(new { message = WorkerBrowserProviderMessages.SyncQueued });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restart(Guid workerId, CancellationToken ct)
    {
        var (success, error) = await workers.SendWorkerCommandAsync(workerId, WorkerCommands.Restart, ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось отправить команду." });
        }

        return Ok(new { message = "Команда перезапуска отправлена воркеру." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(Guid workerId, Guid accountId, bool enabled, CancellationToken ct)
    {
        var (success, error) = await workers.UpdateWorkerAccountAsync(workerId, accountId, enabled, ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось изменить статус аккаунта." });
        }

        return Ok(new
        {
            message = enabled ? "Аккаунт включён в панели." : "Аккаунт отключён в панели.",
            isEnabledInPanel = enabled
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAccount(
        Guid workerId,
        Guid accountId,
        CancellationToken ct)
    {
        // Чекбокс без hidden: при включении шлёт только value=true; при выключении поле не уходит в форму.
        // Старый hidden value=false ломал включение — model binder брал первое значение (false).
        var isEnabledInPanel = Request.Form.TryGetValue("isEnabledInPanel", out var value)
            && string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        var (success, error) = await workers.UpdateWorkerAccountAsync(workerId, accountId, isEnabledInPanel, ct);
        if (!success)
        {
            TempData["WorkersError"] = error;
        }
        else
        {
            TempData["WorkersSuccess"] = isEnabledInPanel
                ? "Аккаунт включён в панели."
                : "Аккаунт отключён в панели.";
        }

        return RedirectToAction(nameof(Details), WorkerDetailsRoute(workerId, Request.Form["sort"], Request.Form["dir"]));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLocalAccount(
        Guid workerId,
        string displayName,
        string? localUserDataDir,
        CancellationToken ct)
    {
        var (success, error) = await workers.CreateLocalAccountAsync(
            workerId,
            displayName,
            localUserDataDir,
            ct);
        TempData[success ? "WorkersSuccess" : "WorkersError"] = success
            ? (string.IsNullOrWhiteSpace(localUserDataDir)
                ? "Браузерный аккаунт создан. Откройте браузер и войдите в Avito."
                : "Аккаунт обычного браузера добавлен.")
            : error;
        return RedirectToAction(nameof(Details), new { id = workerId, provider = WorkerAccountCatalogFilter.LocalProvider });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLocalAccount(
        Guid workerId,
        Guid accountId,
        string? displayName,
        string? localUserDataDir,
        CancellationToken ct)
    {
        var (success, error) = await workers.UpdateLocalAccountAsync(
            workerId,
            accountId,
            displayName,
            localUserDataDir,
            ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось сохранить аккаунт обычного браузера." });
        }

        return Ok(new { message = "Аккаунт обычного браузера сохранён." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLocalAccount(
        Guid workerId,
        Guid accountId,
        CancellationToken ct)
    {
        var (success, error) = await workers.DeleteLocalAccountAsync(workerId, accountId, ct);
        TempData[success ? "WorkersSuccess" : "WorkersError"] = success
            ? "Аккаунт удалён. Папка профиля на диске не удалялась."
            : error;
        return RedirectToAction(nameof(Details), new { id = workerId, provider = WorkerAccountCatalogFilter.LocalProvider });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenLocalBrowser(
        Guid workerId,
        Guid accountId,
        CancellationToken ct)
    {
        var (success, error) = await workers.OpenLocalBrowserAsync(workerId, accountId, ct);
        TempData[success ? "WorkersSuccess" : "WorkersError"] = success
            ? "На машине воркера открывается Chrome. Войдите в Avito и закройте браузер."
            : error;
        return RedirectToAction(nameof(Details), new { id = workerId, provider = WorkerAccountCatalogFilter.LocalProvider });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAccountCredentials(
        Guid workerId,
        Guid accountId,
        string? login,
        string? password,
        bool clear,
        CancellationToken ct)
    {
        var (success, error) = await workers.UpdateWorkerAccountCredentialsAsync(
            workerId,
            accountId,
            login,
            password,
            clear,
            ct);
        if (!success)
        {
            return BadRequest(new { error = error ?? "Не удалось сохранить логин/пароль Avito." });
        }

        return Ok(new
        {
            message = clear
                ? "Логин и пароль Avito удалены."
                : "Логин и пароль Avito сохранены. Воркер подхватит их при следующей синхронизации.",
            hasCredentials = !clear,
            login = string.IsNullOrWhiteSpace(login) ? null : login.Trim()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLocalAccountProfile(
        Guid workerId,
        Guid accountId,
        string? login,
        string? password,
        bool clearCredentials,
        bool? proxyEnabled,
        string? proxyAddress,
        string? proxyUsername,
        string? proxyPassword,
        bool clearProxyPassword,
        string? trafficMode,
        bool? blockMedia,
        bool? blockAnalytics,
        bool? blockImages,
        bool? blockFonts,
        bool? blockPrefetch,
        int? navigationTimeoutSeconds,
        CancellationToken ct)
    {
        var (profile, error) = await workers.UpdateLocalAccountProfileAsync(
            workerId,
            accountId,
            login,
            password,
            clearCredentials,
            proxyEnabled,
            proxyAddress,
            proxyUsername,
            proxyPassword,
            clearProxyPassword,
            trafficMode,
            blockMedia,
            blockAnalytics,
            blockImages,
            blockFonts,
            blockPrefetch,
            navigationTimeoutSeconds,
            ct);
        if (profile is null)
        {
            return BadRequest(new { error = error ?? "Не удалось сохранить настройки профиля." });
        }

        return Ok(new
        {
            message = clearCredentials
                ? "Учётные данные Avito очищены."
                : "Настройки профиля сохранены. Воркер подхватит их при следующей синхронизации.",
            profile
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTopUpSession(
        Guid workerId,
        Guid accountId,
        string subProfileId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return BadRequest(new { error = "Нужно выбрать субпрофиль для пополнения." });
        }

        var result = await workers.CreateTopUpSessionAsync(workerId, accountId, subProfileId, ct);
        if (result.Error is not null)
        {
            return result.ConflictSessionId.HasValue
                ? Conflict(new { error = result.Error, sessionId = result.ConflictSessionId })
                : BadRequest(new { error = result.Error });
        }

        if (result.Session is null)
        {
            return BadRequest(new { error = "Не удалось создать сессию пополнения." });
        }

        return Ok(result.Session);
    }

    [HttpGet]
    public async Task<IActionResult> GetTopUpSession(Guid sessionId, CancellationToken ct)
    {
        var session = await workers.GetTopUpSessionAsync(sessionId, ct);
        return session is null ? NotFound() : Ok(session);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelTopUpSession(Guid sessionId, CancellationToken ct)
    {
        var (success, error) = await workers.CancelTopUpSessionAsync(sessionId, ct);
        return success ? Ok(new { message = "Сессия пополнения отменена." }) : BadRequest(new { error });
    }

    private IActionResult RedirectAfterWorkerAction(Guid workerId, string? returnTo) =>
        string.Equals(returnTo, "index", StringComparison.OrdinalIgnoreCase)
            ? RedirectToAction(nameof(Index))
            : RedirectToAction(nameof(Details), WorkerDetailsRoute(workerId));

    private object WorkerDetailsRoute(
        Guid workerId,
        string? sort = null,
        string? dir = null) => new
    {
        id = workerId,
        sort = sort ?? Request.Query["sort"].ToString(),
        dir = dir ?? Request.Query["dir"].ToString()
    };
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class WorkersController(IWorkersService workers) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? q, string? status, int page = 1, CancellationToken ct = default)
    {
        var model = await workers.GetIndexAsync(q, status, page, ct);
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
    public async Task<IActionResult> Details(
        Guid id,
        string? logsQ,
        string? logsLevel,
        DateTime? logsDate,
        int logsPage = 1,
        CancellationToken ct = default)
    {
        var isAdmin = User.IsInRole(PanelRoles.Admin);
        var model = await workers.GetDetailsAsync(
            id,
            logsQ,
            logsLevel,
            logsDate,
            logsPage,
            includeLogs: isAdmin,
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
        CancellationToken ct)
    {
        var (success, error) = await workers.UpdateWorkerSettingsAsync(
            workerId,
            maxConcurrentAccounts,
            adsPowerApiBaseUrl,
            adsPowerApiKey,
            ct);
        if (!success)
        {
            TempData["WorkersError"] = error;
        }
        else
        {
            TempData["WorkersSuccess"] = "Настройки воркера сохранены.";
        }

        return RedirectToAction(nameof(Details), new { id = workerId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enable(Guid workerId, string? returnTo, CancellationToken ct)
    {
        var (success, error) = await workers.SetWorkerEnabledAsync(workerId, true, ct);
        TempData[success ? "WorkersSuccess" : "WorkersError"] = success
            ? "Воркер включён."
            : error;
        return RedirectAfterWorkerAction(workerId, returnTo);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disable(Guid workerId, string? returnTo, CancellationToken ct)
    {
        var (success, error) = await workers.SetWorkerEnabledAsync(workerId, false, ct);
        TempData[success ? "WorkersSuccess" : "WorkersError"] = success
            ? "Воркер приостановлен."
            : error;
        return RedirectAfterWorkerAction(workerId, returnTo);
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
    public async Task<IActionResult> UpdateAccount(
        Guid workerId,
        Guid accountId,
        bool isEnabledInPanel,
        CancellationToken ct)
    {
        var (success, error) = await workers.UpdateWorkerAccountAsync(workerId, accountId, isEnabledInPanel, ct);
        if (!success)
        {
            TempData["WorkersError"] = error;
        }

        return RedirectToAction(nameof(Details), new { id = workerId });
    }

    private IActionResult RedirectAfterWorkerAction(Guid workerId, string? returnTo) =>
        string.Equals(returnTo, "index", StringComparison.OrdinalIgnoreCase)
            ? RedirectToAction(nameof(Index))
            : RedirectToAction(nameof(Details), new { id = workerId });
}
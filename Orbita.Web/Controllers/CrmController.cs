using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Orbita.Contracts;
using Orbita.Web.Authorization;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize(Roles = $"{OrbitaRoles.Admin},{OrbitaRoles.Manager}")]
public sealed class CrmController(
    OrbitaApiClient api,
    IOfficeContext officeContext,
    OrbitaAuthService auth,
    IOptions<DesignPreviewOptions> previewOptions) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        Guid? officeId,
        string? search,
        string? scope,
        string? city,
        string? vacancy,
        bool overdueOnly = false,
        bool activeLoadOnly = false,
        bool includeClosed = false,
        CancellationToken ct = default)
    {
        officeId = ResolveOfficeId(officeId);
        if (User.IsInRole(OrbitaRoles.Admin) && officeId is null)
        {
            ViewData["CrmUnavailableMessage"] =
                "Выберите офис в переключателе в шапке, чтобы открыть CRM.";
            return View("Unavailable");
        }

        var (board, errorCode) = await api.GetCrmBoardResultAsync(
            officeId,
            new CrmBoardQuery(search, scope, city, vacancy, overdueOnly, activeLoadOnly, includeClosed),
            ct);
        if (board is null)
        {
            if (errorCode is "unauthorized" or "no_session")
            {
                await auth.SignOutAsync(ct);
                return RedirectToAction("Login", "Account");
            }

            ViewData["CrmUnavailableMessage"] = errorCode switch
            {
                "forbidden" => "Нет доступа к CRM. Нужна роль менеджера или администратора.",
                "bad_request" when officeId is null =>
                    "Менеджеру не назначен офис. Обратитесь к администратору или войдите снова.",
                "bad_request" => "Выберите офис в переключателе в шапке, чтобы открыть CRM.",
                "not_found" => "Офис не найден.",
                _ when officeId is null =>
                    "Менеджеру не назначен офис. Обратитесь к администратору или войдите снова.",
                _ => "Не удалось загрузить CRM для выбранного офиса. Выйдите и войдите снова."
            };
            return View("Unavailable");
        }

        return View(board);
    }

    [HttpGet]
    public async Task<IActionResult> Snapshot(
        Guid? officeId,
        string? search,
        string? scope,
        string? city,
        string? vacancy,
        bool overdueOnly = false,
        bool activeLoadOnly = false,
        bool includeClosed = false,
        CancellationToken ct = default)
    {
        officeId = ResolveOfficeId(officeId);
        if (officeId is null)
        {
            return NoContent();
        }

        var (board, errorCode) = await api.GetCrmBoardResultAsync(
            officeId,
            new CrmBoardQuery(search, scope, city, vacancy, overdueOnly, activeLoadOnly, includeClosed),
            ct);
        return board is null ? NoContent() : PartialView("_CrmWorkspace", board);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveAjax(Guid id, string stage, string? comment, CancellationToken ct = default)
    {
        var (ok, error) = await api.MoveCrmCardAsync(id, stage, comment, ct);
        return Json(new { ok, error });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartShift(CancellationToken ct = default)
    {
        var (_, error) = await api.StartCrmShiftAsync(ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StopShift(CancellationToken ct = default)
    {
        var (_, error) = await api.StopCrmShiftAsync(ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Card(Guid id, string? tab, CancellationToken ct = default)
    {
        var card = await api.GetCrmCardAsync(id, ct);
        if (card is null) return NotFound();
        ViewData["CrmTab"] = tab is "tasks" or "history" or "chat" ? tab : "activity";
        ViewData["IsCrmAdmin"] = User.IsInRole(OrbitaRoles.Admin);
        ViewData["CurrentCrmUserId"] = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return View(card);
    }

    [HttpGet]
    public async Task<IActionResult> Tasks(string? scope, CancellationToken ct = default)
    {
        var officeId = ResolveOfficeId(null);
        if (User.IsInRole(OrbitaRoles.Admin) && officeId is null)
        {
            ViewData["CrmUnavailableMessage"] =
                "Выберите офис в переключателе в шапке, чтобы открыть CRM.";
            return View("Unavailable");
        }

        var tasksRequest = api.GetCrmTasksAsync(officeId, ct);
        var boardRequest = api.GetCrmBoardResultAsync(officeId, ct: ct);
        await Task.WhenAll(tasksRequest, boardRequest);
        var tasks = await tasksRequest;
        var (board, boardError) = await boardRequest;
        if (boardError is "unauthorized" or "no_session")
        {
            await auth.SignOutAsync(ct);
            return RedirectToAction("Login", "Account");
        }

        if (tasks is null || board is null)
        {
            ViewData["CrmUnavailableMessage"] = "Не удалось загрузить задачи CRM. Выйдите и войдите снова.";
            return View("Unavailable");
        }

        var selectedScope = scope?.ToLowerInvariant() switch
        {
            "overdue" or "today" or "later" or "completed" or "cancelled" => scope.ToLowerInvariant(),
            _ => "all"
        };

        return View(new CrmTasksViewModel(
            tasks,
            board.Managers,
            board.OpenTaskCount,
            board.OverdueTaskCount,
            selectedScope,
            User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            User.IsInRole(OrbitaRoles.Admin)));
    }

    [HttpGet]
    public async Task<IActionResult> TaskDetails(Guid id, CancellationToken ct = default)
    {
        var task = await api.GetCrmTaskAsync(id, ct);
        return task is null ? NotFound() : View(task);
    }

    [HttpGet]
    public async Task<IActionResult> Team(CancellationToken ct = default)
    {
        var officeId = ResolveOfficeId(null);
        if (officeId is null)
        {
            ViewData["CrmUnavailableMessage"] =
                "Выберите офис в переключателе в шапке, чтобы открыть команду CRM.";
            return View("Unavailable");
        }

        var (board, errorCode) = await api.GetCrmBoardResultAsync(
            officeId,
            query: new CrmBoardQuery(Scope: CrmBoardScopes.Team),
            ct: ct);
        if (errorCode is "unauthorized" or "no_session")
        {
            await auth.SignOutAsync(ct);
            return RedirectToAction("Login", "Account");
        }

        if (board is null)
        {
            ViewData["CrmUnavailableMessage"] = "Не удалось загрузить CRM для выбранного офиса. Выйдите и войдите снова.";
            return View("Unavailable");
        }

        if (!board.IsAdmin) return Forbid();
        return View(board);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Move(Guid id, string stage, string? comment, string? returnUrl, CancellationToken ct = default)
    {
        var (_, error) = await api.MoveCrmCardAsync(id, stage, comment, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectAfterCardMutation(returnUrl, nameof(Card), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLoad(Guid id, bool active, string? returnUrl, CancellationToken ct = default)
    {
        var (_, error) = await api.SetCrmCardActiveLoadAsync(id, active, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectAfterCardMutation(returnUrl, nameof(Card), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(Guid id, string managerUserId, string? returnUrl, CancellationToken ct = default)
    {
        var (_, error) = await api.AssignCrmCardAsync(id, managerUserId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectAfterCardMutation(returnUrl, nameof(Card), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(Guid id, string reason, string? comment, CancellationToken ct = default)
    {
        var (_, error) = await api.CloseCrmCardAsync(id, reason, comment, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(Guid id, CancellationToken ct = default)
    {
        var (_, error) = await api.ReopenCrmCardAsync(id, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNote(Guid id, string text, CancellationToken ct = default)
    {
        var (_, error) = await api.AddCrmNoteAsync(id, text, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FollowUp(Guid id, int minutes, string? title, CancellationToken ct = default)
    {
        var (_, error) = await api.CreateCrmFollowUpAsync(id, minutes, title, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id, tab = "tasks" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTask(Guid? cardId, string title, string? description, string assigneeUserId, DateTime? dueAtUtc, string? importance, string? returnUrl, CancellationToken ct = default)
    {
        var (_, error) = await api.CreateCrmTaskAsync(new CrmTaskCreateRequest(cardId, title, description, assigneeUserId, dueAtUtc, importance ?? CrmTaskImportances.Medium), ct);
        if (error is not null) TempData["CrmError"] = error;
        return cardId is Guid id
            ? RedirectAfterCardMutation(returnUrl, nameof(Card), new { id, tab = "tasks" })
            : RedirectToAction(nameof(Tasks));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteTask(Guid taskId, Guid? cardId, CancellationToken ct = default)
    {
        var (_, error) = await api.CompleteCrmTaskAsync(taskId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return cardId is Guid id
            ? RedirectToAction(nameof(Card), new { id, tab = "tasks" })
            : RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTask(
        Guid taskId,
        string title,
        string? description,
        string assigneeUserId,
        DateTime? dueAtUtc,
        string? importance,
        CancellationToken ct = default)
    {
        var (_, error) = await api.UpdateCrmTaskAsync(
            taskId,
            new CrmTaskUpdateRequest(title, description, assigneeUserId, dueAtUtc, importance ?? CrmTaskImportances.Medium),
            ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelTask(Guid taskId, CancellationToken ct = default)
    {
        var (_, error) = await api.CancelCrmTaskAsync(taskId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReopenTask(Guid taskId, CancellationToken ct = default)
    {
        var (_, error) = await api.ReopenCrmTaskAsync(taskId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTaskComment(Guid taskId, string text, CancellationToken ct = default)
    {
        var (_, error) = await api.AddCrmTaskCommentAsync(taskId, text, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(CrmTaskAttachmentLimits.MaxFileSizeBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = CrmTaskAttachmentLimits.MaxFileSizeBytes)]
    public async Task<IActionResult> UploadTaskAttachment(Guid taskId, IFormFile? file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            TempData["CrmError"] = "Выберите файл для загрузки.";
            return RedirectToAction(nameof(TaskDetails), new { id = taskId });
        }

        if (file.Length > CrmTaskAttachmentLimits.MaxFileSizeBytes)
        {
            TempData["CrmError"] = "Размер вложения не должен превышать 20 МБ.";
            return RedirectToAction(nameof(TaskDetails), new { id = taskId });
        }

        await using var content = file.OpenReadStream();
        var (_, error) = await api.UploadCrmTaskAttachmentAsync(
            taskId,
            content,
            file.Length,
            file.FileName,
            file.ContentType,
            ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpGet]
    public async Task<IActionResult> DownloadTaskAttachment(Guid taskId, Guid attachmentId, CancellationToken ct = default)
    {
        var result = await api.OpenCrmTaskAttachmentAsync(taskId, attachmentId, ct);
        return result.Stream is null
            ? NotFound()
            : File(
                result.Stream,
                result.ContentType ?? "application/octet-stream",
                result.FileName ?? "Вложение");
    }

    private Guid? ResolveOfficeId(Guid? officeId) =>
        officeId
        ?? officeContext.EffectiveOfficeId
        ?? (previewOptions.Value.Enabled ? DesignPreviewData.PreviewOfficeId : null);

    private IActionResult RedirectAfterCardMutation(string? returnUrl, string fallbackAction, object fallbackRouteValues)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToAction(fallbackAction, fallbackRouteValues)!;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = OrbitaRoles.Admin)]
    public async Task<IActionResult> SaveOfficeSettings(Guid officeId, bool isEnabled, bool requireStageComment, CancellationToken ct = default)
    {
        var (_, error) = await api.SetCrmOfficeSettingsAsync(officeId, isEnabled, requireStageComment, ct);
        if (error is not null) TempData["CrmError"] = error;
        else TempData["CrmOk"] = "Настройки CRM сохранены.";
        return RedirectToAction(nameof(Team));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = OrbitaRoles.Admin)]
    public async Task<IActionResult> SaveOfficeFunnel(Guid officeId, string? stagesText, bool resetDefault = false, CancellationToken ct = default)
    {
        IReadOnlyList<string> stages = resetDefault
            ? CrmStages.Default
            : (stagesText ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var (_, error) = await api.SetCrmOfficeFunnelAsync(officeId, stages, ct);
        if (error is not null) TempData["CrmError"] = error;
        else TempData["CrmOk"] = resetDefault ? "Воронка сброшена к значениям по умолчанию." : "Воронка офиса сохранена.";
        return RedirectToAction(nameof(Team));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = OrbitaRoles.Admin)]
    public async Task<IActionResult> SaveCapacity(string managerUserId, int capacity, CancellationToken ct = default)
    {
        var (_, error) = await api.SetCrmManagerCapacityAsync(managerUserId, capacity, ct: ct);
        if (error is not null) TempData["CrmError"] = error;
        else TempData["CrmOk"] = "Ёмкость обновлена.";
        return RedirectToAction(nameof(Team));
    }
}

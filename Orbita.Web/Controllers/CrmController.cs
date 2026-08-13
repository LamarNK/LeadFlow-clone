using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Orbita.Contracts;
using Orbita.Web.Authorization;
using Orbita.Web.Helpers;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class CrmController(
    OrbitaApiClient api,
    IOfficeContext officeContext,
    OrbitaAuthService auth,
    IOptions<DesignPreviewOptions> previewOptions) : Controller
{
    [HttpGet]
    [Authorize(Policy = PanelPermissions.CrmAnalytics)]
    public async Task<IActionResult> Analytics(
        string? from,
        string? to,
        string? managerUserId,
        CancellationToken ct = default)
    {
        var tz = BrowserTimeZone.Resolve(HttpContext);
        var period = string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)
            ? DashboardPeriod.CreateLastDays(30, tz)
            : DashboardPeriod.Parse(from, to, tz);
        var (fromUtc, toUtc) = LocalCalendarDateRange.ToUtcRange(period);
        var isAdmin = PanelRoles.HasElevatedOfficeAccess(User);
        var selectedManagerUserId = isAdmin && !string.IsNullOrWhiteSpace(managerUserId)
            ? managerUserId.Trim()
            : null;
        var analytics = await api.GetCrmAnalyticsAsync(
            fromUtc,
            toUtc,
            officeContext.EffectiveOfficeId,
            selectedManagerUserId,
            ct);

        return View(new CrmAnalyticsViewModel
        {
            Header = new PageHeaderViewModel
            {
                Title = "Аналитика CRM",
                Subtitle = "Воронка, результаты и нагрузка команды",
                ShowRefresh = true,
                ShowDateRange = true,
                DateRangeLabel = period.Label,
                DateFrom = period.From,
                DateTo = period.To,
                DateMax = period.LocalToday,
                ActivePeriodPreset = period.ActivePreset,
                UpdatedAtUtc = analytics?.GeneratedAtUtc ?? DateTime.UtcNow
            },
            Analytics = analytics,
            IsAdmin = isAdmin,
            CurrentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            SelectedManagerUserId = analytics?.ManagerUserId ?? selectedManagerUserId,
            OfficeContextLabel = officeContext.ContextLabel
                                 ?? analytics?.Funnels.FirstOrDefault()?.OfficeName
                                 ?? "Мой офис",
            ErrorMessage = analytics is null
                ? "Не удалось загрузить CRM-аналитику. Обновите страницу или войдите в панель снова."
                : null
        });
    }

    [HttpGet]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    public async Task<IActionResult> Index(
        Guid? officeId,
        string? search,
        string? scope,
        string? city,
        string? vacancy,
        string? managerUserId = null,
        string? closeReason = null,
        bool overdueOnly = false,
        bool activeLoadOnly = false,
        bool includeClosed = false,
        CancellationToken ct = default)
    {
        officeId = ResolveOfficeId(officeId);
        if (PanelRoles.IsGlobalAdmin(User) && officeId is null)
        {
            ViewData["CrmUnavailableMessage"] =
                "Выберите офис в переключателе в шапке, чтобы открыть CRM.";
            return View("Unavailable");
        }

        var effectiveScope = ResolveBoardScope(scope, managerUserId);
        var (board, errorCode) = await api.GetCrmBoardResultAsync(
            officeId,
            new CrmBoardQuery(
                search,
                effectiveScope,
                city,
                vacancy,
                overdueOnly,
                activeLoadOnly,
                includeClosed,
                managerUserId,
                closeReason),
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

        ViewData["CurrentCrmUserId"] = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return View(board);
    }

    [HttpGet]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    public async Task<IActionResult> Snapshot(
        Guid? officeId,
        string? search,
        string? scope,
        string? city,
        string? vacancy,
        string? managerUserId = null,
        string? closeReason = null,
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

        var effectiveScope = ResolveBoardScope(scope, managerUserId);
        var (board, errorCode) = await api.GetCrmBoardResultAsync(
            officeId,
            new CrmBoardQuery(
                search,
                effectiveScope,
                city,
                vacancy,
                overdueOnly,
                activeLoadOnly,
                includeClosed,
                managerUserId,
                closeReason),
            ct);
        ViewData["CurrentCrmUserId"] = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return board is null ? NoContent() : PartialView("_CrmWorkspace", board);
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveAjax(Guid id, string stage, string? comment, CancellationToken ct = default)
    {
        var (ok, error) = await api.MoveCrmCardAsync(id, stage, comment, ct);
        return Json(new { ok, error });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartShift(CancellationToken ct = default)
    {
        var (_, error) = await api.StartCrmShiftAsync(ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StopShift(CancellationToken ct = default)
    {
        var (_, error) = await api.StopCrmShiftAsync(ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    public async Task<IActionResult> Card(Guid id, string? tab, CancellationToken ct = default)
    {
        var canAccessTasks = User.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmTasks)
            || User.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Crm);
        if (tab == "tasks" && !canAccessTasks)
        {
            return Forbid();
        }

        var resolvedTab = tab is "tasks" or "history" or "chat" ? tab : "activity";
        var card = await api.GetCrmCardAsync(id, ct);
        if (card is null) return NotFound();
        ViewData["CrmTab"] = resolvedTab;
        ViewData["CanAccessCrmTasks"] = canAccessTasks;
        ViewData["IsCrmAdmin"] = PanelRoles.HasElevatedOfficeAccess(User);
        ViewData["CurrentCrmUserId"] = User.FindFirstValue(ClaimTypes.NameIdentifier);
        // Chat mark-read is POST-only (see MarkChatRead) so GET stays free of side effects.
        ViewData["MarkChatReadOnLoad"] = resolvedTab == "chat" && card.ChatUnreadCount > 0;
        return View(card);
    }

    [HttpGet("/Crm/Cards/{id:guid}/Avatar")]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    public async Task<IActionResult> Avatar(Guid id, CancellationToken ct = default)
    {
        var result = await api.GetCrmCardAvatarAsync(id, ct);
        return result.Stream is null
            ? NotFound()
            : File(result.Stream, result.ContentType ?? "image/jpeg");
    }

    [HttpGet]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
    public async Task<IActionResult> Tasks(
        string? scope,
        string? managerUserId = null,
        CancellationToken ct = default)
    {
        var officeId = ResolveOfficeId(null);
        if (PanelRoles.IsGlobalAdmin(User) && officeId is null)
        {
            ViewData["CrmUnavailableMessage"] =
                "Выберите офис в переключателе в шапке, чтобы открыть CRM.";
            return View("Unavailable");
        }

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var canFilterResponsible = PanelRoles.HasElevatedOfficeAccess(User);
        var managerFilterSubmitted = managerUserId is not null
                                     || Request.Query.ContainsKey("managerUserId");
        var showAllOfficeTasks = string.Equals(managerUserId, "all", StringComparison.OrdinalIgnoreCase)
                                 || string.IsNullOrWhiteSpace(managerUserId);
        string? selectedManagerUserId;
        if (!canFilterResponsible)
        {
            selectedManagerUserId = currentUserId;
        }
        else if (User.IsInRole(PanelRoles.SeniorManager) && !managerFilterSubmitted)
        {
            selectedManagerUserId = currentUserId;
        }
        else
        {
            selectedManagerUserId = showAllOfficeTasks ? null : managerUserId!.Trim();
        }

        var tasksRequest = api.GetCrmTasksAsync(
            officeId,
            ct,
            selectedManagerUserId);
        var managersRequest = api.GetCrmTaskManagersAsync(officeId, ct);
        await Task.WhenAll(tasksRequest, managersRequest);
        var tasks = await tasksRequest;
        var managers = await managersRequest;
        if (tasks is null || managers is null)
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
            managers,
            selectedScope,
            currentUserId,
            canFilterResponsible,
            selectedManagerUserId,
            canFilterResponsible,
            ResolveBrowserUtcOffsetMinutes()));
    }

    [HttpGet]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
    public async Task<IActionResult> TaskDetails(
        Guid id,
        bool fromTeam = false,
        string? teamTaskScope = null,
        string? taskScope = null,
        string? managerUserId = null,
        CancellationToken ct = default)
    {
        var task = await api.GetCrmTaskAsync(id, ct);
        if (task is null) return NotFound();

        ViewData["TaskListUrl"] = fromTeam
            ? Url.Action(nameof(Team), new { taskScope = teamTaskScope })
            : Url.Action(nameof(Tasks), new { scope = taskScope, managerUserId });
        ViewData["TaskListTitle"] = fromTeam ? "Команда CRM" : "Задачи";
        return View(task);
    }

    [HttpGet]
    [Authorize(Policy = PanelPermissions.CrmTeam)]
    public async Task<IActionResult> Team(string? taskScope, CancellationToken ct = default)
    {
        var officeId = ResolveOfficeId(null);
        if (officeId is null)
        {
            ViewData["CrmUnavailableMessage"] =
                "Выберите офис в переключателе в шапке, чтобы открыть команду CRM.";
            return View("Unavailable");
        }

        var boardRequest = api.GetCrmBoardResultAsync(
            officeId,
            query: new CrmBoardQuery(Scope: CrmBoardScopes.Team),
            ct: ct);
        var tasksRequest = api.GetCrmTasksAsync(officeId, ct);
        await Task.WhenAll(boardRequest, tasksRequest);
        var (board, errorCode) = await boardRequest;
        var tasks = await tasksRequest;
        if (errorCode is "unauthorized" or "no_session")
        {
            await auth.SignOutAsync(ct);
            return RedirectToAction("Login", "Account");
        }

        if (board is null || tasks is null)
        {
            ViewData["CrmUnavailableMessage"] = "Не удалось загрузить CRM для выбранного офиса. Выйдите и войдите снова.";
            return View("Unavailable");
        }

        if (!board.IsAdmin) return Forbid();
        ViewData["CurrentCrmUserId"] = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var selectedTaskScope = taskScope?.ToLowerInvariant() switch
        {
            "overdue" or "today" or "later" or "completed" or "cancelled" => taskScope.ToLowerInvariant(),
            _ => "all"
        };

        var canManageStaff = OfficeStaffRules.CanManageStaff(User);
        IReadOnlyList<PanelUserDto> staff = [];
        if (canManageStaff)
        {
            staff = await api.GetOfficeStaffUsersAsync(officeId, ct) ?? [];
        }

        return View(new CrmTeamViewModel(board, tasks, selectedTaskScope, canManageStaff, staff));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTeam)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateOfficeStaff(
        string fullName,
        string email,
        string password,
        string role,
        CancellationToken ct = default)
    {
        if (!OfficeStaffRules.CanManageStaff(User))
        {
            return Forbid();
        }

        var officeId = ResolveOfficeId(null);
        var (success, error) = await api.CreateOfficeStaffUserAsync(email, fullName, password, role, officeId, ct);
        TempData[success ? "CrmOk" : "CrmError"] = success ? "Сотрудник добавлен." : error;
        return RedirectToAction(nameof(Team));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTeam)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateOfficeStaffFullName(
        string userId,
        string fullName,
        CancellationToken ct = default)
    {
        if (!OfficeStaffRules.CanManageStaff(User))
        {
            return Forbid();
        }

        var officeId = ResolveOfficeId(null);
        var (success, error) = await api.UpdateOfficeStaffFullNameAsync(userId, fullName, officeId, ct);
        TempData[success ? "CrmOk" : "CrmError"] = success ? "ФИО обновлено." : error;
        return RedirectToAction(nameof(Team));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTeam)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateOfficeStaffRole(
        string userId,
        string role,
        CancellationToken ct = default)
    {
        if (!OfficeStaffRules.CanManageStaff(User))
        {
            return Forbid();
        }

        var officeId = ResolveOfficeId(null);
        var (success, error) = await api.UpdateOfficeStaffRoleAsync(userId, role, officeId, ct);
        TempData[success ? "CrmOk" : "CrmError"] = success ? "Должность обновлена." : error;
        return RedirectToAction(nameof(Team));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTeam)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateOfficeStaff(
        string userId,
        string fullName,
        string role,
        string? password,
        int? capacity,
        CancellationToken ct = default)
    {
        if (!OfficeStaffRules.CanManageStaff(User))
        {
            return Forbid();
        }

        var officeId = ResolveOfficeId(null);
        var (success, error) = await api.UpdateOfficeStaffFullNameAsync(userId, fullName, officeId, ct);
        if (!success)
        {
            TempData["CrmError"] = error;
            return RedirectToAction(nameof(Team));
        }

        (success, error) = await api.UpdateOfficeStaffRoleAsync(userId, role, officeId, ct);
        if (!success)
        {
            TempData["CrmError"] = error;
            return RedirectToAction(nameof(Team));
        }

        if (!string.IsNullOrWhiteSpace(password))
        {
            (success, error) = await api.ResetOfficeStaffPasswordAsync(userId, password, officeId, ct);
            if (!success)
            {
                TempData["CrmError"] = error;
                return RedirectToAction(nameof(Team));
            }
        }

        if (capacity is not null)
        {
            var (_, capacityError) = await api.SetCrmManagerCapacityAsync(userId, capacity.Value, ct: ct);
            if (capacityError is not null)
            {
                TempData["CrmError"] = capacityError;
                return RedirectToAction(nameof(Team));
            }
        }

        TempData["CrmOk"] = "Изменения сотрудника сохранены.";
        return RedirectToAction(nameof(Team));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTeam)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetOfficeStaffPassword(
        string userId,
        string password,
        CancellationToken ct = default)
    {
        if (!OfficeStaffRules.CanManageStaff(User))
        {
            return Forbid();
        }

        var officeId = ResolveOfficeId(null);
        var (success, error) = await api.ResetOfficeStaffPasswordAsync(userId, password, officeId, ct);
        TempData[success ? "CrmOk" : "CrmError"] = success ? "Пароль обновлён." : error;
        return RedirectToAction(nameof(Team));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTeam)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LockOfficeStaff(string userId, CancellationToken ct = default)
    {
        if (!OfficeStaffRules.CanManageStaff(User))
        {
            return Forbid();
        }

        var officeId = ResolveOfficeId(null);
        var (success, error) = await api.LockOfficeStaffUserAsync(userId, officeId, ct);
        TempData[success ? "CrmOk" : "CrmError"] = success ? "Сотрудник заблокирован." : error;
        return RedirectToAction(nameof(Team));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTeam)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlockOfficeStaff(string userId, CancellationToken ct = default)
    {
        if (!OfficeStaffRules.CanManageStaff(User))
        {
            return Forbid();
        }

        var officeId = ResolveOfficeId(null);
        var (success, error) = await api.UnlockOfficeStaffUserAsync(userId, officeId, ct);
        TempData[success ? "CrmOk" : "CrmError"] = success ? "Сотрудник разблокирован." : error;
        return RedirectToAction(nameof(Team));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTeam)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOfficeStaff(string userId, CancellationToken ct = default)
    {
        if (!OfficeStaffRules.CanManageStaff(User))
        {
            return Forbid();
        }

        var officeId = ResolveOfficeId(null);
        var (success, error) = await api.DeleteOfficeStaffUserAsync(userId, officeId, ct);
        TempData[success ? "CrmOk" : "CrmError"] = success ? "Сотрудник удалён." : error;
        return RedirectToAction(nameof(Team));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Move(Guid id, string stage, string? comment, string? returnUrl, CancellationToken ct = default)
    {
        var (_, error) = await api.MoveCrmCardAsync(id, stage, comment, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectAfterCardMutation(returnUrl, nameof(Card), new { id });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLoad(Guid id, bool active, string? returnUrl, string? stage, CancellationToken ct = default)
    {
        var (_, error) = await api.SetCrmCardActiveLoadAsync(id, active, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectAfterCardMutation(returnUrl, nameof(Card), new { id, stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(Guid id, string managerUserId, string? returnUrl, string? stage, CancellationToken ct = default)
    {
        var (_, error) = await api.AssignCrmCardAsync(id, managerUserId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectAfterCardMutation(returnUrl, nameof(Card), new { id, stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCard(
        Guid id,
        string fullName,
        string phoneRaw,
        int? age,
        string city,
        string? tab,
        string? stage,
        CancellationToken ct = default)
    {
        var current = await api.GetCrmCardAsync(id, ct);
        string? error;
        if (current is null)
        {
            error = "Карточка не найдена.";
        }
        else
        {
            // Values maintained by CRM integrations (including source IDs) are preserved server-side.
            (_, error) = await api.UpdateCrmCardAsync(
                id,
                new CrmCardUpdateRequest(
                    fullName,
                    phoneRaw,
                    city,
                    current.Card.Vacancy,
                    age,
                    current.Card.SourceResponseId,
                    current.Card.AccountName,
                    current.Card.SourceUrl,
                    current.Card.VacancyUrl,
                    current.Card.MessengerUrl),
                ct);
        }
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id, tab, stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(Guid id, string reason, string? comment, string? stage, CancellationToken ct = default)
    {
        var (_, error) = await api.CloseCrmCardAsync(id, reason, comment, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id, stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPhone(Guid id, string phoneRaw, string? label, bool setAsPrimary, string? stage, CancellationToken ct = default)
    {
        var (_, error) = await api.AddCrmContactPhoneAsync(id, phoneRaw, label, setAsPrimary, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id, stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemovePhone(Guid id, Guid phoneId, string? stage, CancellationToken ct = default)
    {
        var (_, error) = await api.RemoveCrmContactPhoneAsync(id, phoneId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id, stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPrimaryPhone(Guid id, Guid phoneId, string? stage, CancellationToken ct = default)
    {
        var (_, error) = await api.SetCrmPrimaryPhoneAsync(id, phoneId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id, stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkChatRead(Guid id, string? stage, CancellationToken ct = default)
    {
        await api.MarkCrmChatReadAsync(id, ct);
        // Always JSON: this action is only invoked via XHR after the chat tab is shown.
        return Ok(new { ok = true });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendChat(Guid id, string text, string? stage, CancellationToken ct = default)
    {
        var (_, error) = await api.QueueCrmChatMessageAsync(id, text, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id, tab = "chat", stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelChat(Guid id, Guid messageId, string? stage, CancellationToken ct = default)
    {
        var (_, error) = await api.CancelCrmChatMessageAsync(id, messageId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id, tab = "chat", stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateManual(
        string fullName,
        string phoneRaw,
        string? city,
        string? vacancy,
        int? age,
        string? source,
        string? sourceResponseId,
        string? stage,
        bool assignToMe = false,
        CancellationToken ct = default)
    {
        if (!PanelRoles.HasElevatedOfficeAccess(User) && !PanelRoles.IsCrmDeskRole(User))
        {
            return Forbid();
        }

        var (cardId, error) = await api.CreateManualCrmCardAsync(
            new CrmManualCardCreateRequest(fullName, phoneRaw, city, vacancy, age, source, sourceResponseId, stage, assignToMe),
            ct);
        if (error is not null || cardId is null)
        {
            TempData["CrmError"] = error ?? "Не удалось создать отклик.";
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(Card), new { id = cardId.Value, stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(Guid id, string? stage, CancellationToken ct = default)
    {
        var (_, error) = await api.ReopenCrmCardAsync(id, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id, stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNote(Guid id, string text, string? stage, CancellationToken ct = default)
    {
        var (_, error) = await api.AddCrmNoteAsync(id, text, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id, stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateNote(
        Guid id,
        Guid noteId,
        string text,
        string? stage,
        CancellationToken ct = default)
    {
        var (success, error) = await api.UpdateCrmNoteAsync(id, noteId, text, ct);
        var isInlineRequest = string.Equals(
            Request.Headers["X-Requested-With"].ToString(),
            "XMLHttpRequest",
            StringComparison.OrdinalIgnoreCase);
        if (isInlineRequest)
        {
            return success
                ? Ok(new { text = text.Trim(), updatedAtUtc = DateTime.UtcNow })
                : BadRequest(new { error = error ?? "Не удалось изменить комментарий." });
        }

        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id, stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteNote(Guid id, Guid noteId, CancellationToken ct = default)
    {
        var (_, error) = await api.DeleteCrmNoteAsync(id, noteId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PinNote(Guid id, Guid noteId, bool isPinned, CancellationToken ct = default)
    {
        var (_, error) = await api.SetCrmNotePinnedAsync(id, noteId, isPinned, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmBoard)]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FollowUp(Guid id, int minutes, string? title, string? stage, CancellationToken ct = default)
    {
        var (_, error) = await api.CreateCrmFollowUpAsync(id, minutes, title, ct);
        if (error is not null) TempData["CrmError"] = error;
        return RedirectToAction(nameof(Card), new { id, tab = "tasks", stage });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTask(Guid? cardId, string? description, string assigneeUserId, DateTime? dueAtUtc, string? importance, string? taskType, string? returnUrl, string? stage, CancellationToken ct = default)
    {
        var normalizedTaskType = taskType ?? string.Empty;
        var title = CrmTaskTypes.IsValid(normalizedTaskType)
            ? CrmTaskTypes.GetLabel(normalizedTaskType)
            : string.Empty;
        var (_, error) = await api.CreateCrmTaskAsync(new CrmTaskCreateRequest(
            cardId,
            title,
            description,
            assigneeUserId,
            dueAtUtc,
            importance ?? CrmTaskImportances.Medium,
            normalizedTaskType), ct);
        if (error is not null) TempData["CrmError"] = error;
        return cardId is Guid id
            ? RedirectAfterCardMutation(returnUrl, nameof(Card), new { id, tab = "tasks", stage })
            : RedirectToAction(nameof(Tasks));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteTask(Guid taskId, Guid? cardId, string? comment, string? stage, CancellationToken ct = default)
    {
        var (_, error) = await api.CompleteCrmTaskAsync(taskId, comment ?? string.Empty, ct);
        if (error is not null) TempData["CrmError"] = error;
        return cardId is Guid id
            ? RedirectToAction(nameof(Card), new { id, tab = "tasks", stage })
            : RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTask(
        Guid taskId,
        string? description,
        string assigneeUserId,
        DateTime? dueAtUtc,
        string? importance,
        string? taskType,
        Guid? cardId,
        CancellationToken ct = default)
    {
        var normalizedTaskType = taskType ?? string.Empty;
        var title = CrmTaskTypes.IsValid(normalizedTaskType)
            ? CrmTaskTypes.GetLabel(normalizedTaskType)
            : string.Empty;
        var (_, error) = await api.UpdateCrmTaskAsync(
            taskId,
            new CrmTaskUpdateRequest(
                title,
                description,
                assigneeUserId,
                dueAtUtc,
                importance ?? CrmTaskImportances.Medium,
                normalizedTaskType),
            ct);
        if (error is not null) TempData["CrmError"] = error;
        return cardId is Guid id
            ? RedirectToAction(nameof(Card), new { id, tab = "tasks" })
            : RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelTask(Guid taskId, Guid? cardId, CancellationToken ct = default)
    {
        var (_, error) = await api.CancelCrmTaskAsync(taskId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return cardId is Guid id
            ? RedirectToAction(nameof(Card), new { id, tab = "tasks" })
            : RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReopenTask(Guid taskId, Guid? cardId, CancellationToken ct = default)
    {
        var (_, error) = await api.ReopenCrmTaskAsync(taskId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return cardId is Guid id
            ? RedirectToAction(nameof(Card), new { id, tab = "tasks" })
            : RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTask(Guid taskId, Guid? cardId, CancellationToken ct = default)
    {
        var (_, error) = await api.DeleteCrmTaskAsync(taskId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return cardId is Guid id
            ? RedirectToAction(nameof(Card), new { id, tab = "tasks" })
            : RedirectToAction(nameof(Tasks));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTaskComment(Guid taskId, Guid? cardId, string text, CancellationToken ct = default)
    {
        var (_, error) = await api.AddCrmTaskCommentAsync(taskId, text, ct);
        if (error is not null) TempData["CrmError"] = error;
        return cardId is Guid id
            ? RedirectToAction(nameof(Card), new { id, tab = "tasks" })
            : RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTaskComment(
        Guid taskId,
        Guid commentId,
        Guid? cardId,
        string text,
        CancellationToken ct = default)
    {
        var (_, error) = await api.UpdateCrmTaskCommentAsync(taskId, commentId, text, ct);
        if (error is not null) TempData["CrmError"] = error;
        return cardId is Guid id
            ? RedirectToAction(nameof(Card), new { id, tab = "tasks" })
            : RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTaskComment(
        Guid taskId,
        Guid commentId,
        Guid? cardId,
        CancellationToken ct = default)
    {
        var (_, error) = await api.DeleteCrmTaskCommentAsync(taskId, commentId, ct);
        if (error is not null) TempData["CrmError"] = error;
        return cardId is Guid id
            ? RedirectToAction(nameof(Card), new { id, tab = "tasks" })
            : RedirectToAction(nameof(TaskDetails), new { id = taskId });
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTasks)]
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
    [Authorize(Policy = PanelPermissions.CrmTasks)]
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

    private string ResolveBoardScope(string? requestedScope, string? managerUserId)
    {
        var scope = string.IsNullOrWhiteSpace(requestedScope)
            ? User.IsInRole(PanelRoles.Admin) || User.IsInRole(PanelRoles.OfficeLead)
                ? CrmBoardScopes.Team
                : CrmBoardScopes.Mine
            : requestedScope;

        var managerFilterSubmitted = managerUserId is not null || Request.Query.ContainsKey("managerUserId");
        if (!PanelRoles.HasElevatedOfficeAccess(User)
            || !managerFilterSubmitted
            || scope is not (CrmBoardScopes.Mine or CrmBoardScopes.Team))
        {
            return scope;
        }

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return scope == CrmBoardScopes.Mine
               && !string.IsNullOrWhiteSpace(managerUserId)
               && string.Equals(managerUserId, currentUserId, StringComparison.Ordinal)
            ? CrmBoardScopes.Mine
            : CrmBoardScopes.Team;
    }

    private int ResolveBrowserUtcOffsetMinutes()
    {
        const string cookieName = "orbita_utc_offset_minutes";
        return Request.Cookies.TryGetValue(cookieName, out var raw)
               && int.TryParse(raw, out var offset)
            ? Math.Clamp(offset, -14 * 60, 14 * 60)
            : 0;
    }

    private IActionResult RedirectAfterCardMutation(string? returnUrl, string fallbackAction, object fallbackRouteValues)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToAction(fallbackAction, fallbackRouteValues)!;
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTeam)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveOfficeSettings(
        Guid officeId,
        bool isEnabled,
        bool requireStageComment,
        bool deadlineNotificationsEnabled,
        CancellationToken ct = default)
    {
        var (_, error) = await api.SetCrmOfficeSettingsAsync(
            officeId,
            isEnabled,
            requireStageComment,
            deadlineNotificationsEnabled,
            ct);
        if (error is not null) TempData["CrmError"] = error;
        else TempData["CrmOk"] = "Настройки CRM сохранены.";
        return RedirectToAction(nameof(Team));
    }

    [HttpPost]
    [Authorize(Policy = PanelPermissions.CrmTeam)]
    [ValidateAntiForgeryToken]
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
    [Authorize(Policy = PanelPermissions.CrmTeam)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCapacity(string managerUserId, int capacity, CancellationToken ct = default)
    {
        var (_, error) = await api.SetCrmManagerCapacityAsync(managerUserId, capacity, ct: ct);
        if (error is not null) TempData["CrmError"] = error;
        else TempData["CrmOk"] = "Ёмкость обновлена.";
        return RedirectToAction(nameof(Team));
    }
}

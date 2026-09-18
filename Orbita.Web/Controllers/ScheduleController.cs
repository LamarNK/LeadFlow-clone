using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize(Policy = PanelPermissions.Schedule)]
public sealed class ScheduleController(IWorkerScheduleWebService schedule, IOfficeContext officeContext, OrbitaApiClient api) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(int? dayOff, CancellationToken ct)
    {
        var data = officeContext.ShowAllOffices ? null : await schedule.GetAsync(dayOff, ct);
        var selected = data?.Groups.FirstOrDefault(x => x.DayOff == Math.Clamp(dayOff ?? CurrentDay(data.Settings.TimeZoneId), 0, 6)) ?? data?.Groups.FirstOrDefault();
        return View(new ScheduleViewModel
        {
            Header = new PageHeaderViewModel { Title = "Расписание", Subtitle = "6 рабочих дней, полный выходной и автоматическая смена дневного и ночного окна", ShowRefresh = true, UpdatedAtUtc = data?.Settings.UpdatedAtUtc ?? DateTime.UtcNow },
            Schedule = data,
            SelectedGroup = selected,
            NeedsOfficeSelection = officeContext.ShowAllOffices,
            Error = data is null && !officeContext.ShowAllOffices ? "Не удалось загрузить расписание." : null
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign([FromBody] AddWorkerScheduleRequest request, CancellationToken ct) => Result(await api.AssignScheduleWorkersAsync(request, ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Move([FromBody] MoveWorkerScheduleRequest request, CancellationToken ct) => Result(await api.MoveScheduleWorkerAsync(request, ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove([FromBody] RemoveWorkerScheduleRequest request, CancellationToken ct) => Result(await api.RemoveScheduleWorkerAsync(request.WorkerId, ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings([FromBody] UpdateWorkerScheduleSettingsRequest request, CancellationToken ct) => Result(await api.UpdateScheduleSettingsAsync(request, ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Group(Guid id, [FromBody] UpdateWorkerScheduleGroupRequest request, CancellationToken ct) => Result(await api.UpdateScheduleGroupAsync(id, request, ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AutoDistribute(CancellationToken ct) => Result(await api.AutoDistributeScheduleAsync(ct));

    private IActionResult Result((bool Success, string? Error) result) => result.Success ? Ok(new { message = "Расписание сохранено." }) : BadRequest(new { error = result.Error ?? "Не удалось сохранить расписание." });

    private static int CurrentDay(string timeZoneId)
    {
        DateTime local;
        try { local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)); }
        catch { local = DateTime.UtcNow; }
        return local.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)local.DayOfWeek - 1;
    }
}

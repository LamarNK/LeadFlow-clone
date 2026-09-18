using System.Security.Claims;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Api.Endpoints;

public static class WorkerScheduleEndpoints
{
    public static void Map(WebApplication app)
    {
        var schedule = app.MapGroup("/api/v1/schedule").RequireAuthorization(PanelPermissions.Schedule);

        schedule.MapGet("", async (Guid? officeId, int? selectedDayOff, WorkerScheduleManager manager, OfficeScopeService scopes, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var scope = await scopes.ResolveAsync(principal, ct);
            var model = await manager.GetAsync(scope, officeId, selectedDayOff, ct);
            return model is null ? Results.Forbid() : Results.Ok(model);
        });

        schedule.MapPost("/assign", async (AddWorkerScheduleRequest request, Guid? officeId, WorkerScheduleManager manager, OfficeScopeService scopes, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var scope = await scopes.ResolveAsync(principal, ct);
            var result = await manager.AddAsync(scope, officeId, request, principal.FindFirstValue(ClaimTypes.NameIdentifier), ct);
            return result.Conflict is not null ? Results.Conflict(result.Conflict) : result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
        });

        schedule.MapPost("/move", async (MoveWorkerScheduleRequest request, Guid? officeId, WorkerScheduleManager manager, OfficeScopeService scopes, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var scope = await scopes.ResolveAsync(principal, ct);
            var result = await manager.MoveAsync(scope, officeId, request, principal.FindFirstValue(ClaimTypes.NameIdentifier), ct);
            return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
        });

        schedule.MapPost("/remove", async (RemoveWorkerScheduleRequest request, Guid? officeId, WorkerScheduleManager manager, OfficeScopeService scopes, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var scope = await scopes.ResolveAsync(principal, ct);
            var result = await manager.RemoveAsync(scope, officeId, request.WorkerId, ct);
            return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
        });

        schedule.MapPut("/settings", async (UpdateWorkerScheduleSettingsRequest request, Guid? officeId, WorkerScheduleManager manager, OfficeScopeService scopes, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var scope = await scopes.ResolveAsync(principal, ct);
            var result = await manager.UpdateSettingsAsync(scope, officeId, request, principal.FindFirstValue(ClaimTypes.NameIdentifier), ct);
            return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
        });

        schedule.MapPut("/groups/{groupId:guid}", async (Guid groupId, UpdateWorkerScheduleGroupRequest request, Guid? officeId, WorkerScheduleManager manager, OfficeScopeService scopes, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var scope = await scopes.ResolveAsync(principal, ct);
            var result = await manager.UpdateGroupAsync(scope, officeId, groupId, request, ct);
            return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
        });

        schedule.MapPost("/auto-distribute", async (Guid? officeId, WorkerScheduleManager manager, OfficeScopeService scopes, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var scope = await scopes.ResolveAsync(principal, ct);
            var result = await manager.AutoDistributeAsync(scope, officeId, ct);
            return result.Success ? Results.Ok(result.Result) : Results.BadRequest(new { error = result.Error });
        });
    }
}

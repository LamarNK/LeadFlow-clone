using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Http.Features;
using Orbita.Api.Auth;
using Orbita.Api.Data;
using Orbita.Api.Hubs;
using Orbita.Api.Models;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;
using Orbita.Logging.Audit;
using static Orbita.Api.Endpoints.EndpointRequestContext;

namespace Orbita.Api.Endpoints;

public static class CrmEndpoints
{
    public static void Map(WebApplication app)
    {
        var crm = app.MapGroup("/api/v1/crm").RequireAuthorization(PanelPermissions.Crm);
        crm.MapGet("/board", async (
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct,
            Guid? officeId = null,
            string? search = null,
            string? scopeFilter = null,
            string? city = null,
            string? vacancy = null,
            // Defaults: missing non-nullable bool query params otherwise → HTTP 400.
            bool overdueOnly = false,
            bool activeLoadOnly = false,
            bool includeClosed = false) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var isAdmin = principal.IsInRole(PanelRoles.Admin);
            var isManager = principal.IsInRole(PanelRoles.Manager);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId) || (!isAdmin && !isManager))
            {
                return Results.Forbid();
            }

            if (effectiveOfficeId is not Guid resolvedOfficeId)
            {
                return Results.BadRequest(new
                {
                    error = isAdmin
                        ? "Выберите офис в переключателе, чтобы открыть CRM."
                        : "Менеджеру не назначен офис. Обратитесь к администратору."
                });
            }

            var board = await workspace.GetBoardAsync(
                resolvedOfficeId,
                userId,
                isAdmin,
                new CrmBoardQuery(search, scopeFilter, city, vacancy, overdueOnly, activeLoadOnly, includeClosed),
                ct);
            return board is null
                ? Results.NotFound(new { error = "Офис не найден." })
                : Results.Ok(board);
        });

        crm.MapGet("/tasks", async (
            Guid? officeId,
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var isAdmin = principal.IsInRole(PanelRoles.Admin);
            var isManager = principal.IsInRole(PanelRoles.Manager);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId) || (!isAdmin && !isManager))
            {
                return Results.Forbid();
            }

            if (effectiveOfficeId is not Guid resolvedOfficeId)
            {
                return Results.BadRequest(new
                {
                    error = isAdmin
                        ? "Выберите офис в переключателе, чтобы открыть CRM."
                        : "Менеджеру не назначен офис. Обратитесь к администратору."
                });
            }

            return Results.Ok(await workspace.GetTasksAsync(resolvedOfficeId, userId, isAdmin, ct));
        });

        crm.MapPost("/shift/start", async (
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (scope.OfficeId is not Guid officeId || string.IsNullOrWhiteSpace(userId)
                || (!principal.IsInRole(PanelRoles.Manager) && !principal.IsInRole(PanelRoles.Admin)))
            {
                return Results.Forbid();
            }

            return await workspace.StartShiftAsync(officeId, userId, ct) ? Results.NoContent() : Results.NotFound();
        });

        crm.MapPost("/shift/stop", async (
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (scope.OfficeId is not Guid officeId || string.IsNullOrWhiteSpace(userId)
                || (!principal.IsInRole(PanelRoles.Manager) && !principal.IsInRole(PanelRoles.Admin)))
            {
                return Results.Forbid();
            }

            return await workspace.StopShiftAsync(officeId, userId, ct) ? Results.NoContent() : Results.NotFound();
        });

        crm.MapGet("/cards/{cardId:guid}", async (Guid cardId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var card = await workspace.GetCardAsync(cardId, userId, principal.IsInRole(PanelRoles.Admin), ct);
            return card is null ? Results.NotFound() : Results.Ok(card);
        });

        crm.MapPost("/cards/{cardId:guid}/move", async (Guid cardId, CrmMoveRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.MoveAsync(cardId, request.Stage, request.Comment, userId, principal.IsInRole(PanelRoles.Admin), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось сменить этап." });
        });

        crm.MapPost("/cards/{cardId:guid}/assign", async (Guid cardId, CrmAssignRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            return await workspace.AssignAsync(cardId, request.ManagerUserId, userId, principal.IsInRole(PanelRoles.Admin), ct) ? Results.NoContent() : Results.NotFound();
        });

        crm.MapPost("/cards/{cardId:guid}/active-load/{isInActiveLoad:bool}", async (Guid cardId, bool isInActiveLoad, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            return await workspace.SetActiveLoadAsync(cardId, isInActiveLoad, userId, principal.IsInRole(PanelRoles.Admin), ct) ? Results.NoContent() : Results.NotFound();
        });

        crm.MapPost("/cards/{cardId:guid}/close", async (Guid cardId, CrmCloseRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.CloseAsync(cardId, request.Reason, request.Comment, userId, principal.IsInRole(PanelRoles.Admin), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось закрыть карточку." });
        });

        crm.MapPost("/cards/{cardId:guid}/reopen", async (Guid cardId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            return await workspace.ReopenAsync(cardId, userId, principal.IsInRole(PanelRoles.Admin), ct) ? Results.NoContent() : Results.NotFound();
        });

        crm.MapPost("/cards/{cardId:guid}/notes", async (Guid cardId, CrmNoteCreateRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            return await workspace.AddNoteAsync(cardId, request.Text, userId, principal.IsInRole(PanelRoles.Admin), ct) ? Results.NoContent() : Results.BadRequest();
        });

        crm.MapPost("/cards/{cardId:guid}/follow-up", async (Guid cardId, CrmFollowUpRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var task = await workspace.CreateFollowUpAsync(cardId, request.Minutes, request.Title, userId, principal.IsInRole(PanelRoles.Admin), ct);
            return task is null ? Results.BadRequest() : Results.Created($"/api/v1/crm/tasks/{task.Id}", task);
        });

        crm.MapPost("/tasks", async (Guid? officeId, CrmTaskCreateRequest request, CrmWorkspaceService workspace, OfficeScopeService officeScope, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var isAdmin = principal.IsInRole(PanelRoles.Admin);
            if (effectiveOfficeId is not Guid resolvedOfficeId || string.IsNullOrWhiteSpace(userId) || (!isAdmin && !principal.IsInRole(PanelRoles.Manager))) return Results.Forbid();
            var task = await workspace.CreateTaskAsync(resolvedOfficeId, request, userId, isAdmin, ct);
            return task is null ? Results.BadRequest() : Results.Created($"/api/v1/crm/tasks/{task.Id}", task);
        });

        crm.MapPost("/tasks/{taskId:guid}/complete", async (Guid taskId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            return await workspace.CompleteTaskAsync(taskId, userId, principal.IsInRole(PanelRoles.Admin), ct) ? Results.NoContent() : Results.NotFound();
        });

        crm.MapPut("/tasks/{taskId:guid}", async (
            Guid taskId,
            CrmTaskUpdateRequest request,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.UpdateTaskAsync(
                taskId,
                request,
                userId,
                principal.IsInRole(PanelRoles.Admin),
                ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось обновить задачу." });
        });

        crm.MapPost("/tasks/{taskId:guid}/cancel", async (Guid taskId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.CancelTaskAsync(taskId, userId, principal.IsInRole(PanelRoles.Admin), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось отменить задачу." });
        });

        crm.MapPost("/tasks/{taskId:guid}/reopen", async (Guid taskId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.ReopenTaskAsync(taskId, userId, principal.IsInRole(PanelRoles.Admin), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось вернуть задачу в работу." });
        });

        crm.MapGet("/tasks/{taskId:guid}", async (Guid taskId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var task = await workspace.GetTaskAsync(taskId, userId, principal.IsInRole(PanelRoles.Admin), ct);
            return task is null ? Results.NotFound() : Results.Ok(task);
        });

        crm.MapPost("/tasks/{taskId:guid}/comments", async (Guid taskId, CrmTaskCommentCreateRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var comment = await workspace.AddTaskCommentAsync(taskId, request.Text, userId, principal.IsInRole(PanelRoles.Admin), ct);
            return comment is null
                ? Results.BadRequest()
                : Results.Created($"/api/v1/crm/tasks/{taskId:D}#comment-{comment.Id:D}", comment);
        });

        crm.MapPost("/tasks/{taskId:guid}/attachments", async (
            Guid taskId,
            HttpRequest request,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Ожидается multipart/form-data." });
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "Файл не передан." });
            }

            await using var content = file.OpenReadStream();
            var (attachment, error) = await workspace.AddTaskAttachmentAsync(
                taskId,
                content,
                file.Length,
                file.FileName,
                file.ContentType,
                userId,
                principal.IsInRole(PanelRoles.Admin),
                ct);
            return attachment is null
                ? Results.BadRequest(new { error = error ?? "Не удалось сохранить вложение." })
                : Results.Created($"/api/v1/crm/tasks/{taskId:D}/attachments/{attachment.Id:D}", attachment);
        }).DisableAntiforgery();

        crm.MapGet("/tasks/{taskId:guid}/attachments/{attachmentId:guid}", async (
            Guid taskId,
            Guid attachmentId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var attachment = await workspace.OpenTaskAttachmentAsync(
                taskId,
                attachmentId,
                userId,
                principal.IsInRole(PanelRoles.Admin),
                ct);
            return attachment.Stream is null
                ? Results.NotFound()
                : Results.File(
                    attachment.Stream,
                    attachment.ContentType ?? "application/octet-stream",
                    attachment.FileName ?? "Вложение",
                    enableRangeProcessing: true);
        });

        crm.MapGet("/offices/{officeId:guid}/settings", async (Guid officeId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
            principal.IsInRole(PanelRoles.Admin)
                ? Results.Ok(await workspace.GetOfficeSettingsAsync(officeId, ct))
                : Results.Forbid());

        crm.MapPut("/offices/{officeId:guid}/settings", async (Guid officeId, CrmOfficeSettingsRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
            principal.IsInRole(PanelRoles.Admin)
                ? (await workspace.SetOfficeSettingsAsync(officeId, request.IsEnabled, request.RequireStageComment, ct) ? Results.NoContent() : Results.NotFound())
                : Results.Forbid());

        crm.MapPut("/offices/{officeId:guid}/funnel", async (
            Guid officeId,
            CrmOfficeFunnelRequest request,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!principal.IsInRole(PanelRoles.Admin))
            {
                return Results.Forbid();
            }

            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Forbid();
            }

            var (ok, error) = await workspace.SetOfficeFunnelAsync(officeId, request.Stages, userId, ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error });
        });

        crm.MapPut("/managers/{managerUserId}/capacity", async (string managerUserId, Guid? officeId, CrmCapacityRequest request, CrmWorkspaceService workspace, OfficeScopeService officeScope, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (!principal.IsInRole(PanelRoles.Admin)) return Results.Forbid();
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            if (effectiveOfficeId is not Guid resolvedOfficeId) return Results.BadRequest();
            return await workspace.SetCapacityAsync(resolvedOfficeId, managerUserId, request.Capacity, ct) ? Results.NoContent() : Results.BadRequest();
        });

    }
}

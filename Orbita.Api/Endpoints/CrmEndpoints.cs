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
using Orbita.Api.Helpers;
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
        var crmBoard = app.MapGroup("/api/v1/crm").RequireAuthorization(PanelPermissions.CrmBoard);
        var crmTasks = app.MapGroup("/api/v1/crm").RequireAuthorization(PanelPermissions.CrmTasks);
        var crmAnalytics = app.MapGroup("/api/v1/crm").RequireAuthorization(PanelPermissions.CrmAnalytics);
        var crmAdmin = app.MapGroup("/api/v1/crm").RequireAuthorization(PanelPermissions.CrmTeam);

        crmBoard.MapGet("/calls/missed", async (Guid? officeId, DateTime fromUtc, DateTime toUtc,
            string? managerUserId, string? status, int? page, Guid? callId, ClaimsPrincipal principal,
            OfficeScopeService officeScope, CrmMissedCallsQueryService calls, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var scope = await officeScope.ResolveAsync(principal, ct);
            var data = await calls.GetAsync(scope.ResolveFilter(officeId), userId,
                PanelRoles.HasElevatedOfficeAccess(principal), PanelRoles.IsGlobalAdmin(principal),
                DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), DateTime.SpecifyKind(toUtc, DateTimeKind.Utc),
                managerUserId, status, page ?? 1, ct, callId);
            return data is null ? Results.BadRequest(new { error = "Проверьте офис и период (не более года)." }) : Results.Ok(data);
        });

        crmBoard.MapGet("/calls/{callId:guid}/recording", async (
            Guid callId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();

            var recording = await workspace.OpenCallRecordingAsync(
                callId,
                userId,
                PanelRoles.HasElevatedOfficeAccess(principal),
                ct);
            return recording.Stream is null
                ? Results.NotFound()
                : Results.File(
                    recording.Stream,
                    recording.ContentType ?? "audio/wav",
                    recording.FileName ?? $"Звонок-{callId:N}.wav",
                    enableRangeProcessing: true);
        });

        crmBoard.MapGet("/calls/{callId:guid}/ai-insight", async (
            Guid callId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();

            var insight = await workspace.GetCallAiInsightAsync(
                callId,
                userId,
                PanelRoles.HasElevatedOfficeAccess(principal),
                ct);
            return insight is null ? Results.NotFound() : Results.Ok(insight);
        });

        crmAdmin.MapPost("/calls/{callId:guid}/ai-insight/retry", async (
            Guid callId,
            CrmWorkspaceService workspace,
            CrmCallAiProcessingService processing,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!PanelRoles.HasElevatedOfficeAccess(principal)) return Results.Forbid();
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();

            var accessible = await workspace.GetCallAiInsightAsync(callId, userId, true, ct);
            if (accessible is null) return Results.NotFound();
            return await processing.RetryAsync(callId, ct)
                ? Results.Ok(new { status = CrmCallAiStatuses.Pending })
                : Results.NotFound();
        });

        crmBoard.MapGet("/board", async (
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct,
            Guid? officeId = null,
            string? search = null,
            string? scopeFilter = null,
            string? managerUserId = null,
            string? city = null,
            string? vacancy = null,
            string? closeReason = null,
            string? stage = null,
            DateTime? createdFromUtc = null,
            DateTime? createdToUtc = null,
            string? createdFrom = null,
            string? createdTo = null,
            string? view = null,
            int page = 1,
            int pageSize = CrmBoardListOptions.DefaultPageSize,
            string? sort = null,
            string? sortDir = null,
            // Defaults: missing non-nullable bool query params otherwise → HTTP 400.
            bool overdueOnly = false,
            bool activeLoadOnly = false,
            bool includeClosed = false,
            int timeZoneOffsetMinutes = 0) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var isAdmin = PanelRoles.HasElevatedOfficeAccess(principal);
            var isManager = PanelRoles.IsCrmDeskRole(principal);
            var hasCrmBoardAccess = principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmBoard)
                || principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Crm)
                || principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmTeam);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId) || (!isAdmin && !isManager && !hasCrmBoardAccess))
            {
                return Results.Forbid();
            }

            if (effectiveOfficeId is not Guid resolvedOfficeId)
            {
                return Results.BadRequest(new
                {
                    error = PanelRoles.IsGlobalAdmin(principal)
                        ? "Выберите офис в переключателе, чтобы открыть CRM."
                        : "Не назначен офис. Обратитесь к администратору."
                });
            }

            // Elevated roles only: team claim alone must not grant CanEdit / office-wide board powers.
            var effectiveScopeFilter = string.IsNullOrWhiteSpace(scopeFilter)
                ? principal.IsInRole(PanelRoles.Admin) || principal.IsInRole(PanelRoles.OfficeLead)
                    ? CrmBoardScopes.Team
                    : CrmBoardScopes.Mine
                : scopeFilter;
            if (isAdmin
                && !string.IsNullOrWhiteSpace(managerUserId)
                && effectiveScopeFilter is CrmBoardScopes.Mine or CrmBoardScopes.Team)
            {
                effectiveScopeFilter = CrmBoardScopes.Team;
            }

            var board = await workspace.GetBoardAsync(
                resolvedOfficeId,
                userId,
                isAdmin,
                new CrmBoardQuery(
                    search,
                    effectiveScopeFilter,
                    city,
                    vacancy,
                    overdueOnly,
                    activeLoadOnly,
                    includeClosed,
                    managerUserId,
                    closeReason,
                    view,
                    page,
                    pageSize,
                    sort,
                    sortDir,
                    stage,
                    createdFromUtc,
                    createdToUtc,
                    createdFrom,
                    createdTo,
                    timeZoneOffsetMinutes),
                ct);
            return board is null
                ? Results.NotFound(new { error = "Офис не найден." })
                : Results.Ok(board);
        });

        crmAnalytics.MapGet("/analytics", async (
            CrmAnalyticsQueryService analytics,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            DateTime? fromUtc,
            DateTime? toUtc,
            Guid? officeId,
            string? managerUserId,
            string? cohortBasis,
            CancellationToken ct) =>
        {
            var isAdmin = PanelRoles.HasElevatedOfficeAccess(principal);
            var isManager = PanelRoles.IsCrmDeskRole(principal);
            var hasCrmAnalyticsAccess = principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmAnalytics)
                || principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Crm);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId) || (!isAdmin && !isManager && !hasCrmAnalyticsAccess))
            {
                return Results.Forbid();
            }

            if (fromUtc is not DateTime from || toUtc is not DateTime to)
            {
                return Results.BadRequest(new { error = "Укажите fromUtc и toUtc." });
            }

            var scope = await officeScope.ResolveAsync(principal, ct);
            var result = await analytics.GetAsync(
                scope,
                userId,
                isAdmin,
                new CrmAnalyticsQuery(
                    DateTimeUtcHelper.EnsureUtc(from),
                    DateTimeUtcHelper.EnsureUtc(to),
                    officeId,
                    managerUserId, cohortBasis),
                ct);

            return result.Outcome switch
            {
                CrmAnalyticsQueryOutcome.Success => Results.Ok(result.Data),
                CrmAnalyticsQueryOutcome.BadRequest => Results.BadRequest(new { error = result.Error }),
                CrmAnalyticsQueryOutcome.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Forbid()
            };
        });

        crmAnalytics.MapGet("/analytics/evidence", async (
            CrmAnalyticsQueryService analytics, OfficeScopeService officeScope, ClaimsPrincipal principal,
            DateTime fromUtc, DateTime toUtc, string metric, Guid? officeId, string? managerUserId,
            int? page, string? cohortBasis, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var result = await analytics.GetEvidenceAsync(await officeScope.ResolveAsync(principal, ct),
                userId, PanelRoles.HasElevatedOfficeAccess(principal),
                new CrmAnalyticsQuery(fromUtc, toUtc, officeId, managerUserId, cohortBasis), metric, page ?? 1, ct);
            return result.Outcome switch
            {
                CrmAnalyticsQueryOutcome.Success => Results.Ok(result.Data),
                CrmAnalyticsQueryOutcome.BadRequest => Results.BadRequest(),
                CrmAnalyticsQueryOutcome.NotFound => Results.NotFound(),
                _ => Results.Forbid()
            };
        }).RequireAuthorization(PanelPermissions.CrmBoard);

        crmTasks.MapGet("/tasks", async (
            Guid? officeId,
            string? managerUserId,
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var isAdmin = PanelRoles.HasElevatedOfficeAccess(principal);
            var isManager = PanelRoles.IsCrmDeskRole(principal);
            var hasCrmTasksAccess = principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmTasks)
                || principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Crm)
                || principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmTeam);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId) || (!isAdmin && !isManager && !hasCrmTasksAccess))
            {
                return Results.Forbid();
            }

            if (effectiveOfficeId is not Guid resolvedOfficeId)
            {
                return Results.BadRequest(new
                {
                    error = PanelRoles.IsGlobalAdmin(principal)
                        ? "Выберите офис в переключателе, чтобы открыть CRM."
                        : "Не назначен офис. Обратитесь к администратору."
                });
            }

            var selectedManagerUserId = isAdmin && !string.IsNullOrWhiteSpace(managerUserId)
                ? managerUserId.Trim()
                : null;
            return Results.Ok(await workspace.GetTasksAsync(
                resolvedOfficeId,
                userId,
                isAdmin,
                selectedManagerUserId,
                ct));
        });

        crmTasks.MapGet("/tasks/managers", async (
            Guid? officeId,
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var isAdmin = PanelRoles.HasElevatedOfficeAccess(principal);
            var isManager = PanelRoles.IsCrmDeskRole(principal);
            var hasCrmTasksAccess = principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmTasks)
                || principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Crm);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId) || (!isAdmin && !isManager && !hasCrmTasksAccess))
            {
                return Results.Forbid();
            }

            if (effectiveOfficeId is not Guid resolvedOfficeId)
            {
                return Results.BadRequest(new
                {
                    error = PanelRoles.IsGlobalAdmin(principal)
                        ? "Выберите офис в переключателе, чтобы открыть CRM."
                        : "Не назначен офис. Обратитесь к администратору."
                });
            }

            var board = await workspace.GetBoardAsync(
                resolvedOfficeId,
                userId,
                isAdmin,
                new CrmBoardQuery(),
                ct);
            return board is null
                ? Results.NotFound(new { error = "Офис не найден." })
                : Results.Ok(board.Managers);
        });

        crmBoard.MapPost("/shift/start", async (
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (scope.OfficeId is not Guid officeId || string.IsNullOrWhiteSpace(userId)
                || (!PanelRoles.IsCrmDeskRole(principal)
                    && !PanelRoles.HasElevatedOfficeAccess(principal)
                    && !principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmBoard)
                    && !principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Crm)))
            {
                return Results.Forbid();
            }

            return await workspace.StartShiftAsync(officeId, userId, ct) ? Results.NoContent() : Results.NotFound();
        });

        crmBoard.MapPost("/shift/stop", async (
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (scope.OfficeId is not Guid officeId || string.IsNullOrWhiteSpace(userId)
                || (!PanelRoles.IsCrmDeskRole(principal)
                    && !PanelRoles.HasElevatedOfficeAccess(principal)
                    && !principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmBoard)
                    && !principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Crm)))
            {
                return Results.Forbid();
            }

            return await workspace.StopShiftAsync(officeId, userId, ct) ? Results.NoContent() : Results.NotFound();
        });

        crmBoard.MapGet("/cards/{cardId:guid}", async (Guid cardId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var card = await workspace.GetCardAsync(cardId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return card is null ? Results.NotFound() : Results.Ok(card);
        });

        crmBoard.MapDelete("/cards/{cardId:guid}", async (
            Guid cardId,
            CrmWorkspaceService workspace,
            PanelAuditService audit,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Forbid();
            }

            var (ok, error, cardName) = await workspace.DeleteCardAsync(cardId, userId, ct);
            if (!ok)
            {
                if (error == CrmCardDeletionPermission.DeniedMessage) return Results.Forbid();
                return string.Equals(error, "Карточка не найдена.", StringComparison.Ordinal)
                    ? Results.NotFound()
                    : Results.BadRequest(new { error = error ?? "Не удалось удалить карточку." });
            }

            await audit.LogAsync(
                userId,
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditActions.CrmCardDeleted,
                "crm_card",
                cardId.ToString("D"),
                cardName,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);
            return Results.NoContent();
        });

        crmBoard.MapGet("/cards/{cardId:guid}/avatar", async (Guid cardId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var avatar = await workspace.GetCardAvatarAsync(cardId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return avatar is null
                ? Results.NotFound()
                : Results.File(avatar.Bytes, avatar.ContentType);
        });

        crmBoard.MapPost("/cards/bulk/assign", async (
            CrmBulkAssignRequest request,
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId) || !PanelRoles.HasElevatedOfficeAccess(principal))
            {
                return Results.Forbid();
            }

            var selectsWholeStage = !string.IsNullOrWhiteSpace(request.AllCardsInStage);
            if ((!selectsWholeStage && (request.CardIds is null || request.CardIds.Count is < 1 or > 500))
                || string.IsNullOrWhiteSpace(request.ManagerUserId))
            {
                return Results.BadRequest(new { error = "Выберите от 1 до 500 карточек или весь этап и ответственного." });
            }

            if (selectsWholeStage)
            {
                var scope = await officeScope.ResolveAsync(principal, ct);
                var effectiveOfficeId = scope.ResolveFilter(request.OfficeId);
                if (effectiveOfficeId is not Guid officeId)
                {
                    return Results.BadRequest(new { error = "Выберите офис для массового действия." });
                }

                request = request with
                {
                    OfficeId = officeId,
                    AllCardsInStage = request.AllCardsInStage!.Trim(),
                    CardIds = []
                };
            }

            return Results.Ok(await workspace.BulkAssignAsync(request, userId, ct));
        });

        crmBoard.MapPost("/cards/bulk/transition", async (
            CrmBulkTransitionRequest request,
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId) || !PanelRoles.HasElevatedOfficeAccess(principal))
            {
                return Results.Forbid();
            }

            var selectsWholeStage = !string.IsNullOrWhiteSpace(request.AllCardsInStage);
            if ((!selectsWholeStage && (request.CardIds is null || request.CardIds.Count is < 1 or > 500))
                || !CrmBulkTransitionOperations.IsValid(request.Operation))
            {
                return Results.BadRequest(new { error = "Выберите от 1 до 500 карточек или весь этап и действие." });
            }

            if (selectsWholeStage)
            {
                var scope = await officeScope.ResolveAsync(principal, ct);
                var effectiveOfficeId = scope.ResolveFilter(request.OfficeId);
                if (effectiveOfficeId is not Guid officeId)
                {
                    return Results.BadRequest(new { error = "Выберите офис для массового действия." });
                }

                request = request with
                {
                    OfficeId = officeId,
                    AllCardsInStage = request.AllCardsInStage!.Trim(),
                    CardIds = []
                };
            }

            var requiresComment = principal.IsInRole(PanelRoles.SeniorManager)
                                  && !principal.IsInRole(PanelRoles.OfficeLead)
                                  && !principal.IsInRole(PanelRoles.Admin);
            if (requiresComment && string.IsNullOrWhiteSpace(request.Comment))
            {
                return Results.BadRequest(new { error = "Старшему менеджеру необходимо указать причину массового изменения." });
            }

            if (request.Operation == CrmBulkTransitionOperations.Move && string.IsNullOrWhiteSpace(request.Stage))
            {
                return Results.BadRequest(new { error = "Выберите новый этап." });
            }

            if (request.Operation == CrmBulkTransitionOperations.Close && !CrmCloseReasons.IsValid(request.CloseReason))
            {
                return Results.BadRequest(new { error = "Выберите тип закрытия." });
            }

            if (request.Operation == CrmBulkTransitionOperations.Close
                && string.Equals(request.CloseReason, CrmCloseReasons.Success, StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "Успешно закрывайте карточки по одной — для каждой нужен отдельный отчёт с файлами." });
            }

            var auditComment = string.IsNullOrWhiteSpace(request.Comment)
                ? request.Operation == CrmBulkTransitionOperations.Close
                    ? "Массовое закрытие карточек."
                    : "Массовая смена этапа."
                : request.Comment.Trim();
            var normalized = request with { Comment = auditComment };
            return Results.Ok(await workspace.BulkTransitionAsync(normalized, userId, ct));
        });

        crmBoard.MapPost("/cards/{cardId:guid}/move", async (Guid cardId, CrmMoveRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.MoveAsync(cardId, request.Stage, request.Comment, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось сменить этап." });
        });

        crmBoard.MapPost("/cards/{cardId:guid}/assign", async (Guid cardId, CrmAssignRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            return await workspace.AssignAsync(cardId, request.ManagerUserId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct) ? Results.NoContent() : Results.NotFound();
        });

        crmBoard.MapPost("/cards/{cardId:guid}/active-load/{isInActiveLoad:bool}", async (Guid cardId, bool isInActiveLoad, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            return await workspace.SetActiveLoadAsync(cardId, isInActiveLoad, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct) ? Results.NoContent() : Results.NotFound();
        });

        crmBoard.MapPost("/cards/{cardId:guid}/close", async (Guid cardId, CrmCloseRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.CloseAsync(cardId, request.Reason, request.Comment, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось закрыть карточку." });
        });

        crmBoard.MapPost("/cards/{cardId:guid}/close-success", async (
            Guid cardId,
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
            var uploads = form.Files
                .Select(file => new CrmSuccessDocumentUpload(
                    file.Name,
                    file.FileName,
                    file.ContentType,
                    file.Length,
                    file.OpenReadStream))
                .ToList();
            var (ok, error) = await workspace.CloseSuccessAsync(
                cardId,
                form["comment"].ToString(),
                form["contractMissingReason"].ToString(),
                uploads,
                userId,
                PanelRoles.HasElevatedOfficeAccess(principal),
                ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось закрыть карточку в успех." });
        }).DisableAntiforgery();

        crmBoard.MapPut("/cards/{cardId:guid}/success-report", async (
            Guid cardId,
            HttpRequest request,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId) || !PanelRoles.CanEditSuccessReport(principal))
            {
                return Results.Forbid();
            }
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Ожидается multipart/form-data." });
            }

            var form = await request.ReadFormAsync(ct);
            var keptDocumentIds = new List<Guid>();
            foreach (var rawDocumentId in form["keptDocumentIds"])
            {
                if (!Guid.TryParse(rawDocumentId, out var documentId))
                {
                    return Results.BadRequest(new { error = "Передан некорректный идентификатор файла отчёта." });
                }
                keptDocumentIds.Add(documentId);
            }
            var uploads = form.Files
                .Select(file => new CrmSuccessDocumentUpload(
                    file.Name,
                    file.FileName,
                    file.ContentType,
                    file.Length,
                    file.OpenReadStream))
                .ToList();
            var (ok, error) = await workspace.UpdateSuccessReportAsync(
                cardId,
                keptDocumentIds.Distinct().ToArray(),
                form["contractMissingReason"].ToString(),
                uploads,
                userId,
                canEditReport: true,
                ct);
            return ok
                ? Results.NoContent()
                : Results.BadRequest(new { error = error ?? "Не удалось изменить отчёт." });
        }).DisableAntiforgery();

        crmBoard.MapGet("/cards/{cardId:guid}/success-documents/{documentId:guid}", async (
            Guid cardId,
            Guid documentId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var document = await workspace.OpenSuccessDocumentAsync(
                cardId,
                documentId,
                userId,
                PanelRoles.HasElevatedOfficeAccess(principal),
                ct);
            return document.Stream is null
                ? Results.NotFound()
                : Results.File(
                    document.Stream,
                    document.ContentType ?? "application/octet-stream",
                    document.FileName ?? "Документ",
                    enableRangeProcessing: true);
        });

        crmBoard.MapGet("/cards/{cardId:guid}/success-report/archive", async (
            Guid cardId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)
                || !PanelRoles.CanDownloadSuccessReportArchive(principal))
            {
                return Results.Forbid();
            }

            var archive = await workspace.OpenSuccessReportArchiveAsync(
                cardId,
                userId,
                PanelRoles.HasElevatedOfficeAccess(principal),
                ct);
            return archive.Stream is null
                ? Results.NotFound()
                : Results.File(
                    archive.Stream,
                    "application/zip",
                    archive.FileName ?? "Отчёт.zip",
                    enableRangeProcessing: true);
        });

        crmBoard.MapPost("/cards/{cardId:guid}/reopen", async (Guid cardId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            return await workspace.ReopenAsync(cardId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct) ? Results.NoContent() : Results.NotFound();
        });

        crmBoard.MapPost("/cards/{cardId:guid}/notes", async (Guid cardId, CrmNoteCreateRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            return await workspace.AddNoteAsync(cardId, request.Text, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct) ? Results.NoContent() : Results.BadRequest();
        });

        crmBoard.MapPut("/cards/{cardId:guid}", async (
            Guid cardId,
            CrmCardUpdateRequest request,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.UpdateCardAsync(
                cardId,
                request,
                userId,
                PanelRoles.HasElevatedOfficeAccess(principal),
                ct);
            return ok
                ? Results.NoContent()
                : Results.BadRequest(new { error = error ?? "Не удалось сохранить карточку." });
        });

        crmBoard.MapPost("/cards/{cardId:guid}/phones", async (
            Guid cardId,
            CrmContactPhoneCreateRequest request,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (phone, error) = await workspace.AddContactPhoneAsync(
                cardId, request, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return phone is null
                ? Results.BadRequest(new { error = error ?? "Не удалось добавить номер." })
                : Results.Ok(phone);
        });

        crmBoard.MapDelete("/cards/{cardId:guid}/phones/{phoneId:guid}", async (
            Guid cardId,
            Guid phoneId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.RemoveContactPhoneAsync(
                cardId, phoneId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось удалить номер." });
        });

        crmBoard.MapPost("/cards/{cardId:guid}/phones/{phoneId:guid}/primary", async (
            Guid cardId,
            Guid phoneId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.SetPrimaryContactPhoneAsync(
                cardId, phoneId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось назначить основной номер." });
        });

        crmBoard.MapPost("/cards/{cardId:guid}/chat/read", async (
            Guid cardId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            return await workspace.MarkChatReadAsync(cardId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        crmBoard.MapPost("/cards/{cardId:guid}/chat", async (
            Guid cardId,
            CrmChatSendRequest request,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.QueueChatMessageAsync(
                cardId, request.Text, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok
                ? Results.NoContent()
                : Results.BadRequest(new { error = error ?? "Не удалось поставить сообщение в очередь." });
        });

        crmBoard.MapPost("/cards/{cardId:guid}/chat/{messageId:guid}/cancel", async (
            Guid cardId,
            Guid messageId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.CancelChatMessageAsync(
                cardId, messageId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok
                ? Results.NoContent()
                : Results.BadRequest(new { error = error ?? "Не удалось отменить сообщение." });
        });

        crmBoard.MapPost("/cards/manual", async (
            Guid? officeId,
            CrmManualCardCreateRequest request,
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var isAdmin = PanelRoles.HasElevatedOfficeAccess(principal);
            if (!isAdmin && !PanelRoles.IsCrmDeskRole(principal))
            {
                return Results.Forbid();
            }

            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId) || effectiveOfficeId is not Guid resolvedOfficeId)
            {
                return Results.Forbid();
            }

            var (cardId, error) = await workspace.CreateManualCardAsync(
                resolvedOfficeId,
                request,
                userId,
                isAdmin,
                ct);
            return cardId is Guid id
                ? Results.Created($"/api/v1/crm/cards/{id:D}", new CrmManualCardCreateResult(id))
                : Results.BadRequest(new { error = error ?? "Не удалось создать отклик." });
        });

        crmBoard.MapPost("/cards/import-file", async (
            Guid? officeId,
            CrmLeadFileImportRequest request,
            CrmWorkspaceService workspace,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!PanelRoles.HasElevatedOfficeAccess(principal))
            {
                return Results.Forbid();
            }

            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId) || effectiveOfficeId is not Guid resolvedOfficeId)
            {
                return Results.Forbid();
            }

            var (result, error) = await workspace.ImportLeadFileAsync(
                resolvedOfficeId,
                request,
                userId,
                ct);
            return result is not null
                ? Results.Ok(result)
                : Results.BadRequest(new { error = error ?? "Не удалось импортировать лиды." });
        });

        crmBoard.MapPut("/cards/{cardId:guid}/notes/{noteId:guid}", async (
            Guid cardId,
            Guid noteId,
            CrmNoteUpdateRequest request,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.UpdateNoteAsync(
                cardId, noteId, request.Text, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось изменить комментарий." });
        });

        crmBoard.MapDelete("/cards/{cardId:guid}/notes/{noteId:guid}", async (
            Guid cardId,
            Guid noteId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.DeleteNoteAsync(
                cardId, noteId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось удалить комментарий." });
        });

        crmBoard.MapPost("/cards/{cardId:guid}/notes/{noteId:guid}/pin", async (
            Guid cardId,
            Guid noteId,
            CrmNotePinRequest request,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.SetNotePinnedAsync(
                cardId, noteId, request.IsPinned, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось закрепить комментарий." });
        });

        crmBoard.MapPost("/cards/{cardId:guid}/follow-up", async (Guid cardId, CrmFollowUpRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var task = await workspace.CreateFollowUpAsync(cardId, request.Minutes, request.Title, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return task is null ? Results.BadRequest() : Results.Created($"/api/v1/crm/tasks/{task.Id}", task);
        }).RequireAuthorization(PanelPermissions.CrmTasks);

        crmTasks.MapPost("/tasks", async (Guid? officeId, CrmTaskCreateRequest request, CrmWorkspaceService workspace, OfficeScopeService officeScope, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var isAdmin = PanelRoles.HasElevatedOfficeAccess(principal);
            var hasCrmTasksAccess = principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmTasks)
                || principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Crm);
            if (effectiveOfficeId is not Guid resolvedOfficeId
                || string.IsNullOrWhiteSpace(userId)
                || (!isAdmin && !PanelRoles.IsCrmDeskRole(principal) && !hasCrmTasksAccess)) return Results.Forbid();
            var task = await workspace.CreateTaskAsync(resolvedOfficeId, request, userId, isAdmin, ct);
            return task is null ? Results.BadRequest() : Results.Created($"/api/v1/crm/tasks/{task.Id}", task);
        });

        crmTasks.MapPost("/tasks/{taskId:guid}/complete", async (
            Guid taskId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct,
            CrmTaskCompleteRequest? request = null) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            // Missing/empty body → same 400 as empty comment (no 415/model-binding crash).
            var comment = request?.Comment;
            var (ok, error) = await workspace.CompleteTaskAsync(
                taskId,
                comment ?? string.Empty,
                userId,
                PanelRoles.HasElevatedOfficeAccess(principal),
                ct);
            return ok
                ? Results.NoContent()
                : Results.BadRequest(new { error = error ?? "Не удалось выполнить задачу." });
        });

        crmTasks.MapPut("/tasks/{taskId:guid}", async (
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
                PanelRoles.HasElevatedOfficeAccess(principal),
                ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось обновить задачу." });
        });

        crmTasks.MapDelete("/tasks/{taskId:guid}", async (
            Guid taskId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.DeleteTaskAsync(
                taskId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось удалить задачу." });
        });

        crmTasks.MapPost("/tasks/{taskId:guid}/cancel", async (Guid taskId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.CancelTaskAsync(taskId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось отменить задачу." });
        });

        crmTasks.MapPost("/tasks/{taskId:guid}/reopen", async (Guid taskId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.ReopenTaskAsync(taskId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось вернуть задачу в работу." });
        });

        crmTasks.MapGet("/tasks/{taskId:guid}", async (Guid taskId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var task = await workspace.GetTaskAsync(taskId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return task is null ? Results.NotFound() : Results.Ok(task);
        });

        crmTasks.MapGet("/notifications", async (
            Guid? officeId,
            bool? unreadOnly,
            int? limit,
            CrmDeadlineNotificationService notifications,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (effectiveOfficeId is not Guid resolvedOfficeId || string.IsNullOrWhiteSpace(userId))
            {
                return Results.BadRequest(new { error = "Выберите офис, чтобы открыть уведомления CRM." });
            }

            return Results.Ok(await notifications.GetAsync(
                resolvedOfficeId,
                userId,
                unreadOnly == true,
                limit ?? 20,
                ct));
        });

        crmTasks.MapGet("/notifications/summary", async (
            Guid? officeId,
            CrmDeadlineNotificationService notifications,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (effectiveOfficeId is not Guid resolvedOfficeId || string.IsNullOrWhiteSpace(userId))
            {
                return Results.BadRequest(new { error = "Выберите офис, чтобы открыть уведомления CRM." });
            }

            return Results.Ok(await notifications.GetSummaryAsync(resolvedOfficeId, userId, ct));
        });

        crmTasks.MapPost("/notifications/{notificationId:guid}/read", async (
            Guid notificationId,
            Guid? officeId,
            CrmDeadlineNotificationService notifications,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (effectiveOfficeId is not Guid resolvedOfficeId || string.IsNullOrWhiteSpace(userId))
            {
                return Results.BadRequest(new { error = "Выберите офис, чтобы открыть уведомления CRM." });
            }

            return await notifications.MarkReadAsync(resolvedOfficeId, userId, notificationId, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        crmTasks.MapPost("/notifications/read-all", async (
            Guid? officeId,
            CrmDeadlineNotificationService notifications,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? principal.FindFirstValue("sub");
            if (effectiveOfficeId is not Guid resolvedOfficeId || string.IsNullOrWhiteSpace(userId))
            {
                return Results.BadRequest(new { error = "Выберите офис, чтобы открыть уведомления CRM." });
            }

            var updated = await notifications.MarkAllReadAsync(resolvedOfficeId, userId, ct);
            return Results.Ok(new { updated });
        });

        crmTasks.MapPost("/tasks/{taskId:guid}/comments", async (Guid taskId, CrmTaskCommentCreateRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var comment = await workspace.AddTaskCommentAsync(taskId, request.Text, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return comment is null
                ? Results.BadRequest()
                : Results.Created($"/api/v1/crm/tasks/{taskId:D}#comment-{comment.Id:D}", comment);
        });

        crmTasks.MapPut("/tasks/{taskId:guid}/comments/{commentId:guid}", async (
            Guid taskId,
            Guid commentId,
            CrmTaskCommentUpdateRequest request,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.UpdateTaskCommentAsync(
                taskId, commentId, request.Text, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось изменить комментарий." });
        });

        crmTasks.MapDelete("/tasks/{taskId:guid}/comments/{commentId:guid}", async (
            Guid taskId,
            Guid commentId,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Results.Forbid();
            var (ok, error) = await workspace.DeleteTaskCommentAsync(
                taskId, commentId, userId, PanelRoles.HasElevatedOfficeAccess(principal), ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = error ?? "Не удалось удалить комментарий." });
        });

        crmTasks.MapPost("/tasks/{taskId:guid}/attachments", async (
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
                PanelRoles.HasElevatedOfficeAccess(principal),
                ct);
            return attachment is null
                ? Results.BadRequest(new { error = error ?? "Не удалось сохранить вложение." })
                : Results.Created($"/api/v1/crm/tasks/{taskId:D}/attachments/{attachment.Id:D}", attachment);
        }).DisableAntiforgery();

        crmTasks.MapGet("/tasks/{taskId:guid}/attachments/{attachmentId:guid}", async (
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
                PanelRoles.HasElevatedOfficeAccess(principal),
                ct);
            return attachment.Stream is null
                ? Results.NotFound()
                : Results.File(
                    attachment.Stream,
                    attachment.ContentType ?? "application/octet-stream",
                    attachment.FileName ?? "Вложение",
                    enableRangeProcessing: true);
        });

        crmAdmin.MapGet("/offices/{officeId:guid}/settings", async (Guid officeId, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
            CanManageCrmTeam(principal)
                ? Results.Ok(await workspace.GetOfficeSettingsAsync(officeId, ct))
                : Results.Forbid());

        crmAdmin.MapPut("/offices/{officeId:guid}/settings", async (Guid officeId, CrmOfficeSettingsRequest request, CrmWorkspaceService workspace, ClaimsPrincipal principal, CancellationToken ct) =>
            CanManageCrmTeam(principal)
                ? (await workspace.SetOfficeSettingsAsync(
                    officeId,
                    request.IsEnabled,
                    request.RequireStageComment,
                    request.DeadlineNotificationsEnabled,
                    ct) ? Results.NoContent() : Results.NotFound())
                : Results.Forbid());

        crmAdmin.MapPut("/offices/{officeId:guid}/funnel", async (
            Guid officeId,
            CrmOfficeFunnelRequest request,
            CrmWorkspaceService workspace,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!CanManageCrmTeam(principal))
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

        crmAdmin.MapPut("/managers/{managerUserId}/capacity", async (string managerUserId, Guid? officeId, CrmCapacityRequest request, CrmWorkspaceService workspace, OfficeScopeService officeScope, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (!CanManageCrmTeam(principal)) return Results.Forbid();
            var scope = await officeScope.ResolveAsync(principal, ct);
            var effectiveOfficeId = scope.ResolveFilter(officeId);
            if (effectiveOfficeId is not Guid resolvedOfficeId) return Results.BadRequest();
            return await workspace.SetCapacityAsync(resolvedOfficeId, managerUserId, request.Capacity, ct) ? Results.NoContent() : Results.BadRequest();
        });

    }

    private static bool CanManageCrmTeam(ClaimsPrincipal principal) =>
        PanelRoles.HasElevatedOfficeAccess(principal)
        || principal.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmTeam);
}

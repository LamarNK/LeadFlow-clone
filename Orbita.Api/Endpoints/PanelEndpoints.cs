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

public static class PanelEndpoints
{
    public static void Map(WebApplication app)
    {
        var workers = app.MapGroup("/api/v1/panel").RequireAuthorization(PanelPermissions.Workers);
        var events = app.MapGroup("/api/v1/panel").RequireAuthorization(PanelPermissions.Events);
        var settings = app.MapGroup("/api/v1/panel").RequireAuthorization(PanelPermissions.Settings);

        workers.MapPost("/captcha-sessions", async (
            CreateCaptchaSessionRequest request,
            CaptchaSessionService captchaSessions,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var (session, conflict) = await captchaSessions.CreateAsync(request, principal, ct);
            if (conflict is not null)
            {
                return Results.Conflict(new
                {
                    error = conflict.Message,
                    activeSessionId = conflict.ActiveSessionId,
                    activeOperatorDisplayName = conflict.ActiveOperatorDisplayName,
                    activeAccountName = conflict.ActiveAccountName
                });
            }

            return session is null
                ? Results.BadRequest(new { error = "Не удалось создать сессию." })
                : Results.Ok(session);
        });

        workers.MapGet("/captcha-sessions/{id:guid}", async (
            Guid id,
            CaptchaSessionService captchaSessions,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var session = await captchaSessions.GetAsync(id, principal, ct);
            return session is null ? Results.NotFound() : Results.Ok(session);
        });

        workers.MapPost("/captcha-sessions/{id:guid}/cancel", async (
            Guid id,
            CaptchaSessionService captchaSessions,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var (success, error) = await captchaSessions.CancelAsync(id, principal, ct);
            return success ? Results.Ok() : Results.BadRequest(new { error });
        });

        workers.MapGet("/workers/{id:guid}/captcha-lock", async (
            Guid id,
            CaptchaSessionService captchaSessions,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var lockState = await captchaSessions.GetWorkerLockAsync(id, principal, ct);
            return lockState is null ? Results.NotFound() : Results.Ok(lockState);
        });

        workers.MapPost("/workers/{id:guid}/accounts/{accountId:guid}/top-up-sessions", async (
            Guid id,
            Guid accountId,
            TopUpSessionService topUpSessions,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var (session, conflict) = await topUpSessions.CreateAsync(id, accountId, principal, ct);
            if (conflict is not null)
            {
                return Results.Conflict(new
                {
                    error = conflict.Message,
                    activeSessionId = conflict.ActiveSessionId,
                    activeAccountName = conflict.ActiveAccountName
                });
            }

            return session is null
                ? Results.BadRequest(new { error = "Не удалось создать сессию пополнения." })
                : Results.Ok(session);
        });

        workers.MapGet("/top-up-sessions/{id:guid}", async (
            Guid id,
            TopUpSessionService topUpSessions,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var session = await topUpSessions.GetAsync(id, principal, ct);
            return session is null ? Results.NotFound() : Results.Ok(session);
        });

        workers.MapPost("/top-up-sessions/{id:guid}/cancel", async (
            Guid id,
            TopUpSessionService topUpSessions,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var (success, error) = await topUpSessions.CancelAsync(id, principal, ct);
            return success ? Results.Ok() : Results.BadRequest(new { error });
        });

        workers.MapPost("/browser-monitor-sessions", async (
            Guid workerId,
            BrowserMonitorService browserMonitorSessions,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var (session, error) = await browserMonitorSessions.StartAsync(workerId, principal, ct);
            return session is null
                ? Results.BadRequest(new { error = error ?? "Не удалось создать сессию просмотра." })
                : Results.Ok(session);
        });

        workers.MapGet("/browser-monitor-sessions/{id:guid}", async (
            Guid id,
            BrowserMonitorService browserMonitorSessions,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var session = await browserMonitorSessions.GetAsync(id, principal, ct);
            return session is null ? Results.NotFound() : Results.Ok(session);
        });

        workers.MapPost("/browser-monitor-sessions/{id:guid}/stop", async (
            Guid id,
            BrowserMonitorService browserMonitorSessions,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var (success, error) = await browserMonitorSessions.StopAsync(id, principal, ct);
            return success ? Results.Ok() : Results.BadRequest(new { error });
        });

        app.MapGet("/api/v1/workers/browser-monitor-sessions/{id:guid}/alive", (
            Guid id,
            BrowserMonitorService browserMonitorSessions,
            ClaimsPrincipal user) =>
        {
            if (!TryGetWorkerId(user, out var workerId)
                || !browserMonitorSessions.TryAuthorizeWorker(id, workerId, out _))
            {
                return Results.NotFound();
            }

            return Results.Ok();
        }).RequireAuthorization("Worker");

        // Office pick-list for any panel user (operators create workers for a delivery office).
        workers.MapGet("/offices/options", async (
            OfficeAdminService offices,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await offices.ListOptionsAsync(ct));
        });

        workers.MapPost("/workers/create", async (
            CreateWorkerRequest request,
            WorkerAdminService workers,
            PanelAuditService audit,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (result, error) = await workers.CreateAsync(request.DisplayName, request.OfficeId, scope, ct);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditActions.WorkerCreated,
                "worker",
                result!.WorkerId.ToString(),
                result.DisplayName,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            await GlobalLogger.Instance.LogAsync(
                $"Worker created ({result.WorkerId}, {result.DisplayName}).",
                DeskLinkAuditLogLevel.Info);

            return Results.Ok(result);
        });

        workers.MapPost("/workers/{id:guid}/enable", async (
            Guid id,
            WorkerAdminService workers,
            PanelAuditService audit,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (worker, error) = await workers.SetMonitoringPausedAsync(id, false, scope, ct);
            if (error is not null)
            {
                return Results.NotFound(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditActions.WorkerMonitoringResumed,
                "worker",
                id.ToString(),
                worker!.DisplayName,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            return Results.Ok(worker);
        });

        workers.MapPost("/workers/{id:guid}/disable", async (
            Guid id,
            WorkerAdminService workers,
            PanelAuditService audit,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (worker, error) = await workers.SetMonitoringPausedAsync(id, true, scope, ct);
            if (error is not null)
            {
                return Results.NotFound(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditActions.WorkerMonitoringPaused,
                "worker",
                id.ToString(),
                worker!.DisplayName,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            return Results.Ok(worker);
        });

        workers.MapPost("/workers/enable-all", async (
            WorkerAdminService workers,
            PanelAuditService audit,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (result, error) = await workers.SetAllMonitoringPausedAsync(false, scope, ct);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditActions.WorkersMonitoringEnabledAll,
                "workers",
                "bulk",
                $"enabled:{result!.UpdatedCount}",
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            return Results.Ok(result);
        });

        workers.MapPost("/workers/disable-all", async (
            WorkerAdminService workers,
            PanelAuditService audit,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (result, error) = await workers.SetAllMonitoringPausedAsync(true, scope, ct);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditActions.WorkersMonitoringDisabledAll,
                "workers",
                "bulk",
                $"disabled:{result!.UpdatedCount}",
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            return Results.Ok(result);
        });

        workers.MapPost("/workers/{id:guid}/rotate-key", async (
            Guid id,
            WorkerAdminService workers,
            PanelAuditService audit,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (result, error) = await workers.RotateApiKeyAsync(id, scope, ct);
            if (error is not null)
            {
                return Results.NotFound(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditActions.WorkerKeyRotated,
                "worker",
                id.ToString(),
                null,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            return Results.Ok(result);
        });

        workers.MapDelete("/workers/{id:guid}", async (
            Guid id,
            WorkerAdminService workers,
            WorkerDiagnosticsService diagnostics,
            PanelAuditService audit,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (displayName, error) = await workers.DeleteAsync(id, scope, diagnostics, ct);
            if (error is not null)
            {
                return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditActions.WorkerDeleted,
                "worker",
                id.ToString(),
                displayName,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            await GlobalLogger.Instance.LogAsync(
                $"Worker deleted ({id}, {displayName}).",
                DeskLinkAuditLogLevel.Warning);

            return Results.Ok(new { message = "Воркер удалён." });
        });

        events.MapPost("/events/{id:guid}/dismiss", async (
            Guid id,
            WorkerEventService events,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (success, error) = await events.DismissAsync(id, scope, ct);
            if (!success)
            {
                return error is not null && error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            return Results.Ok(new { message = "Событие отмечено как обработанное." });
        });

        settings.MapGet("/me", async (
            PanelUserService panelUsers,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }

            var profile = await panelUsers.GetProfileAsync(userId, ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        settings.MapPost("/me/password", async (
            ChangeOwnPasswordRequest request,
            PanelUserService panelUsers,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }

            var error = await panelUsers.ChangeOwnPasswordAsync(
                userId,
                request.CurrentPassword,
                request.NewPassword,
                GetActor(principal, http),
                ct);
            if (error is null)
            {
                return Results.NoContent();
            }

            return Results.BadRequest(new { error });
        });

        settings.MapGet("/security/policy", (PasswordPolicyService policy) =>
            Results.Ok(policy.GetPolicy()));

        settings.MapGet("/me/integrations/bitrix", async (
            OfficeBitrixIntegrationService officeBitrix,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Unauthorized();
            }

            var integration = await officeBitrix.GetForScopeAsync(scope, ct);
            return integration is null
                ? Results.BadRequest(new { error = "Офис не назначен." })
                : Results.Ok(integration);
        });

        settings.MapGet("/office/integrations/bitrix", async (
            OfficeBitrixIntegrationService bitrix,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var integration = await bitrix.GetForScopeAsync(scope, ct);
            return integration is null
                ? Results.BadRequest(new { error = "Офис не назначен." })
                : Results.Ok(integration);
        });

        settings.MapPut("/office/integrations/bitrix", async (
            SaveBitrixIntegrationRequest request,
            OfficeBitrixIntegrationService bitrix,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess || scope.IsGlobalAdmin || scope.OfficeId is not Guid officeId)
            {
                return Results.Forbid();
            }

            var actor = GetActor(principal, http);
            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var (integration, error) = await bitrix.SaveAsync(
                officeId,
                request.WebhookUrl,
                actorUserId,
                actor.Email,
                actor.IpAddress,
                ct);
            return error is not null
                ? Results.BadRequest(new { error })
                : Results.Ok(integration);
        });

        settings.MapPost("/office/integrations/bitrix/validate", async (
            ValidateBitrixIntegrationRequest request,
            OfficeBitrixIntegrationService bitrix,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess || scope.IsGlobalAdmin || scope.OfficeId is not Guid officeId)
            {
                return Results.Forbid();
            }

            var actor = GetActor(principal, http);
            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var (validation, error) = await bitrix.ValidateAsync(
                officeId,
                request.WebhookUrl,
                actorUserId,
                actor.Email,
                actor.IpAddress,
                persistResult: true,
                ct);
            return error is not null
                ? Results.BadRequest(new { error })
                : Results.Ok(validation);
        });

        settings.MapGet("/office/bitrix-settings", async (
            OfficeBitrixSettingsService bitrixSettings,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            Guid? officeId,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var settings = await bitrixSettings.GetForScopeAsync(scope, officeId, ct);
            return settings is null
                ? Results.BadRequest(new { error = "Офис не назначен." })
                : Results.Ok(settings);
        });

        settings.MapPut("/office/bitrix-settings", async (
            UpdateOfficeBitrixSettingsRequest request,
            OfficeBitrixSettingsService bitrixSettings,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            HttpContext http,
            Guid? officeId,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (settings, error) = await bitrixSettings.UpdateForScopeAsync(
                scope,
                request.TransmissionEnabled,
                principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
                principal.FindFirstValue(ClaimTypes.Email),
                http.Connection.RemoteIpAddress?.ToString(),
                officeId,
                ct);

            return error is not null
                ? Results.BadRequest(new { error })
                : Results.Ok(settings);
        });


    }
}

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

public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin").RequireAuthorization("Admin");
        admin.MapGet("/access-profiles", async (AccessProfileService profiles, CancellationToken ct) =>
            Results.Ok(await profiles.GetAllAsync(ct)));

        admin.MapPut("/access-profiles/{profileId}", async (
            string profileId,
            UpdateAccessProfileRequest request,
            AccessProfileService profiles,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var error = await profiles.UpdateAsync(
                profileId,
                request.Permissions,
                GetActor(principal, http),
                ct);
            return error is null ? Results.NoContent() : Results.BadRequest(new { error });
        });

        admin.MapGet("/users", async (PanelUserService panelUsers, CancellationToken ct) =>
            Results.Ok(await panelUsers.ListAsync(ct)));

        admin.MapPost("/users", async (
            CreatePanelUserRequest request,
            PanelUserService panelUsers,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (user, error) = await panelUsers.CreateAsync(
                request.Email,
                request.Password,
                request.Role,
                request.OfficeId,
                request.FullName,
                GetActor(principal, http),
                ct);
            if (error is not null)
            {
                return error.Contains("уже существует", StringComparison.OrdinalIgnoreCase)
                    ? Results.Conflict(new { error })
                    : Results.BadRequest(new { error });
            }

            return Results.Ok(user);
        });

        admin.MapPut("/users/{id}/full-name", async (
            string id,
            UpdatePanelUserFullNameRequest request,
            PanelUserService panelUsers,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (user, error) = await panelUsers.SetFullNameAsync(
                id,
                request.FullName,
                GetActor(principal, http),
                ct);
            return error is null ? Results.Ok(user) : Results.BadRequest(new { error });
        });

        admin.MapDelete("/users/{id}", async (
            string id,
            PanelUserService panelUsers,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var error = await panelUsers.DeleteAsync(
                id,
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                GetActor(principal, http),
                ct);
            if (error is null)
            {
                return Results.NoContent();
            }

            return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error })
                : Results.BadRequest(new { error });
        });

        admin.MapPost("/users/{id}/password", async (
            string id,
            ResetPanelUserPasswordRequest request,
            PanelUserService panelUsers,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var error = await panelUsers.ResetPasswordAsync(
                id,
                request.Password,
                GetActor(principal, http),
                ct);
            if (error is null)
            {
                return Results.NoContent();
            }

            return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error })
                : Results.BadRequest(new { error });
        });

        admin.MapPut("/users/{id}/office", async (
            string id,
            UpdatePanelUserOfficeRequest request,
            PanelUserService panelUsers,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (user, error) = await panelUsers.SetOfficeAsync(
                id,
                request.OfficeId,
                GetActor(principal, http),
                ct);
            if (error is null)
            {
                return Results.Ok(user);
            }

            return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error })
                : Results.BadRequest(new { error });
        });

        admin.MapPut("/users/{id}/role", async (
            string id,
            UpdatePanelUserRoleRequest request,
            PanelUserService panelUsers,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (user, error) = await panelUsers.SetRoleAsync(
                id,
                request.Role,
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                GetActor(principal, http),
                ct);
            if (error is null)
            {
                return Results.Ok(user);
            }

            return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error })
                : Results.BadRequest(new { error });
        });

        admin.MapPut("/users/{id}/permissions", async (
            string id,
            UpdatePanelUserPermissionsRequest request,
            PanelUserService panelUsers,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (user, error) = await panelUsers.SetPermissionOverrideAsync(
                id,
                request.UseProfilePermissions,
                request.Permissions,
                GetActor(principal, http),
                ct);
            if (error is null)
            {
                return Results.Ok(user);
            }

            return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error })
                : Results.BadRequest(new { error });
        });

        admin.MapPost("/users/{id}/lock", async (
            string id,
            PanelUserService panelUsers,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (user, error) = await panelUsers.LockAsync(
                id,
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                GetActor(principal, http),
                ct);
            if (error is null)
            {
                return Results.Ok(user);
            }

            return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error })
                : Results.BadRequest(new { error });
        });

        admin.MapPost("/users/{id}/unlock", async (
            string id,
            PanelUserService panelUsers,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (user, error) = await panelUsers.UnlockAsync(id, GetActor(principal, http), ct);
            if (error is null)
            {
                return Results.Ok(user);
            }

            return Results.NotFound(new { error });
        });

        admin.MapPost("/users/{id}/revoke-sessions", async (
            string id,
            PanelUserService panelUsers,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var error = await panelUsers.RevokeSessionsAsync(
                id,
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                GetActor(principal, http),
                ct);
            if (error is null)
            {
                return Results.NoContent();
            }

            return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error })
                : Results.BadRequest(new { error });
        });

        admin.MapGet("/integrations/bitrix", async (
            OfficeAdminService offices,
            CancellationToken ct) =>
            Results.Ok(await offices.ListAsync(ct)));

        admin.MapGet("/offices/{id:guid}/integrations/bitrix", async (
            Guid id,
            OfficeBitrixIntegrationService bitrix,
            CancellationToken ct) =>
        {
            var integration = await bitrix.GetForOfficeAsync(id, ct);
            return integration is null ? Results.NotFound() : Results.Ok(integration);
        });

        admin.MapPut("/offices/{id:guid}/integrations/bitrix", async (
            Guid id,
            SaveBitrixIntegrationRequest request,
            OfficeBitrixIntegrationService bitrix,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var actor = GetActor(principal, http);
            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var (integration, error) = await bitrix.SaveAsync(
                id,
                request.WebhookUrl,
                actorUserId,
                actor.Email,
                actor.IpAddress,
                ct);
            if (error is not null)
            {
                return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            return Results.Ok(integration);
        });

        admin.MapPost("/offices/{id:guid}/integrations/bitrix/validate", async (
            Guid id,
            ValidateBitrixIntegrationRequest request,
            OfficeBitrixIntegrationService bitrix,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var actor = GetActor(principal, http);
            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var (validation, error) = await bitrix.ValidateAsync(
                id,
                request.WebhookUrl,
                actorUserId,
                actor.Email,
                actor.IpAddress,
                persistResult: true,
                ct);
            if (error is not null)
            {
                return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            return Results.Ok(validation);
        });

        admin.MapGet("/users/{userId}/integrations/bitrix", async (
            string userId,
            PanelBitrixIntegrationService integrations,
            CancellationToken ct) =>
        {
            var integration = await integrations.GetForUserAsync(userId, ct);
            return integration is null ? Results.NotFound() : Results.Ok(integration);
        });

        admin.MapPut("/users/{userId}/integrations/bitrix", async (
            string userId,
            SaveBitrixIntegrationRequest request,
            PanelBitrixIntegrationService integrations,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var actor = GetActor(principal, http);
            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var (integration, error) = await integrations.SaveAsync(
                userId,
                request.WebhookUrl,
                actorUserId,
                actor.Email,
                actor.IpAddress,
                ct);
            if (error is not null)
            {
                return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            return Results.Ok(integration);
        });

        admin.MapPost("/users/{userId}/integrations/bitrix/validate", async (
            string userId,
            ValidateBitrixIntegrationRequest request,
            PanelBitrixIntegrationService integrations,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var actor = GetActor(principal, http);
            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var (validation, error) = await integrations.ValidateAsync(
                userId,
                request.WebhookUrl,
                actorUserId,
                actor.Email,
                actor.IpAddress,
                persistResult: string.IsNullOrWhiteSpace(request.WebhookUrl),
                ct);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            return Results.Ok(validation);
        });

        admin.MapGet("/offices", async (OfficeAdminService offices, CancellationToken ct) =>
            Results.Ok(await offices.ListAsync(ct)));

        admin.MapGet("/offices/{id:guid}", async (Guid id, OfficeAdminService offices, CancellationToken ct) =>
        {
            var office = await offices.GetAsync(id, ct);
            return office is null ? Results.NotFound() : Results.Ok(office);
        });

        admin.MapPost("/offices", async (
            CreateOfficeRequest request,
            OfficeAdminService offices,
            PanelAuditService audit,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (office, error) = await offices.CreateAsync(request.Name, ct);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditOfficeActions.OfficeCreated,
                "office",
                office!.Id.ToString(),
                office.Name,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            return Results.Ok(office);
        });

        admin.MapPut("/offices/{id:guid}", async (
            Guid id,
            UpdateOfficeRequest request,
            OfficeAdminService offices,
            PanelAuditService audit,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (office, error) = await offices.UpdateAsync(
                id,
                request.Name,
                request.IsEnabled,
                request.BitrixTransmissionEnabled,
                request.CrmEnabled,
                ct);
            if (error is not null)
            {
                return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditOfficeActions.OfficeUpdated,
                "office",
                id.ToString(),
                request.Name,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            return Results.Ok(office);
        });

        admin.MapPost("/offices/{id:guid}/rotate-registration-secret", async (
            Guid id,
            OfficeAdminService offices,
            PanelAuditService audit,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (result, error) = await offices.RotateRegistrationSecretAsync(id, ct);
            if (error is not null)
            {
                return Results.NotFound(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditOfficeActions.OfficeRegistrationRotated,
                "office",
                id.ToString(),
                null,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            return Results.Ok(result);
        });

        admin.MapDelete("/offices/{id:guid}", async (
            Guid id,
            OfficeAdminService offices,
            PanelAuditService audit,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (success, error, officeName) = await offices.DeleteAsync(id, ct);
            if (!success)
            {
                return error is not null && error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditOfficeActions.OfficeDeleted,
                "office",
                id.ToString(),
                officeName,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            return Results.Ok(new { id, name = officeName });
        });

        admin.MapGet("/offices/{id:guid}/registration", async (Guid id, OfficeAdminService offices, CancellationToken ct) =>
        {
            var info = await offices.GetRegistrationInfoAsync(id, ct);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        admin.MapGet("/workers", async (
            Guid? officeId,
            WorkerAdminService workers,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = OfficeScope.GlobalAdmin;
            return Results.Ok(await workers.ListAsync(scope, officeId, ct));
        });

        admin.MapPost("/workers/create", async (
            CreateWorkerRequest request,
            WorkerAdminService workers,
            PanelAuditService audit,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = OfficeScope.GlobalAdmin;
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

        admin.MapPut("/workers/{id:guid}", async (
            Guid id,
            UpdateAdminWorkerRequest request,
            WorkerAdminService workers,
            PanelAuditService audit,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = OfficeScope.GlobalAdmin;
            var (worker, error) = await workers.RenameAsync(id, request.DisplayName, scope, ct);
            if (error is not null)
            {
                return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditActions.WorkerRenamed,
                "worker",
                id.ToString(),
                request.DisplayName,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            await GlobalLogger.Instance.LogAsync(
                $"Worker renamed ({id}, name={request.DisplayName}).",
                DeskLinkAuditLogLevel.Info);

            return Results.Ok(worker);
        });

        admin.MapPost("/workers/{id:guid}/enable", async (
            Guid id,
            WorkerAdminService workers,
            PanelAuditService audit,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = OfficeScope.GlobalAdmin;
            var (worker, error) = await workers.SetEnabledAsync(id, true, scope, ct);
            if (error is not null)
            {
                return Results.NotFound(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditActions.WorkerEnabled,
                "worker",
                id.ToString(),
                worker!.DisplayName,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            await GlobalLogger.Instance.LogAsync(
                $"Worker enabled ({id}, {worker.DisplayName}).",
                DeskLinkAuditLogLevel.Info);

            return Results.Ok(worker);
        });

        admin.MapPost("/workers/{id:guid}/disable", async (
            Guid id,
            WorkerAdminService workers,
            PanelAuditService audit,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = OfficeScope.GlobalAdmin;
            var (worker, error) = await workers.SetEnabledAsync(id, false, scope, ct);
            if (error is not null)
            {
                return Results.NotFound(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditActions.WorkerDisabled,
                "worker",
                id.ToString(),
                worker!.DisplayName,
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            await GlobalLogger.Instance.LogAsync(
                $"Worker disabled ({id}, {worker.DisplayName}).",
                DeskLinkAuditLogLevel.Warning);

            return Results.Ok(worker);
        });

        admin.MapPost("/workers/{id:guid}/rotate-key", async (
            Guid id,
            WorkerAdminService workers,
            PanelAuditService audit,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var scope = OfficeScope.GlobalAdmin;
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

            await GlobalLogger.Instance.LogAsync(
                $"Worker API key rotated ({id}).",
                DeskLinkAuditLogLevel.Warning);

            return Results.Ok(result);
        });

        admin.MapGet("/workers/registration", (WorkerAdminService workers) =>
            Results.Ok(workers.GetRegistrationInfo()));

        admin.MapPost("/leadflow-import/preview", async (
            HttpRequest request,
            LeadFlowImportService importService,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Ожидается multipart/form-data." });
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("databaseFile");
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "Файл databaseFile не передан." });
            }

            if (!Guid.TryParse(form["officeId"].FirstOrDefault(), out var officeId))
            {
                return Results.BadRequest(new { error = "Укажите офис для импорта." });
            }

            var encryptionKey = form["encryptionKey"].FirstOrDefault();
            await using var stream = file.OpenReadStream();
            var (preview, error) = await importService.PreviewAsync(
                stream,
                file.FileName,
                officeId,
                encryptionKey,
                ct);
            return error is null ? Results.Ok(preview) : Results.BadRequest(new { error });
        }).DisableAntiforgery();

        admin.MapPost("/leadflow-import/execute", async (
            LeadFlowImportExecuteRequest request,
            LeadFlowImportService importService,
            PanelAuditService audit,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (result, error) = await importService.ExecuteAsync(request.SessionId, request.SelectedIds, ct);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            await audit.LogAsync(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Email),
                PanelAuditActions.LeadFlowImportExecuted,
                "office",
                null,
                $"imported={result!.Imported}; skipped={result.Skipped}; failed={result.Failed}",
                http.Connection.RemoteIpAddress?.ToString(),
                ct);

            return Results.Ok(result);
        });

        admin.MapGet("/worker-releases", async (WorkerReleaseService releases, CancellationToken ct) =>
            Results.Ok(await releases.ListAsync(ct)));

        admin.MapPost("/worker-releases/upload", async (HttpRequest request, WorkerReleaseService releases, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Ожидается multipart/form-data." });
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("packageFile");
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "Файл packageFile не передан." });
            }

            await using var stream = file.OpenReadStream();
            var (release, error) = await releases.UploadAsync(
                stream,
                file.FileName,
                form["version"].FirstOrDefault(),
                form["releaseNotes"].FirstOrDefault(),
                ct);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            return Results.Ok(release);
        }).DisableAntiforgery();

        admin.MapPost("/worker-releases/set-latest", async (
            SetWorkerReleaseLatestRequest request,
            WorkerReleaseService releases,
            CancellationToken ct) =>
        {
            var (success, error) = await releases.SetLatestAsync(request.Version, ct);
            return success ? Results.Ok() : Results.BadRequest(new { error });
        });

        admin.MapPost("/worker-releases/delete", async (
            DeleteWorkerReleaseRequest request,
            WorkerReleaseService releases,
            CancellationToken ct) =>
        {
            var (success, error) = await releases.DeleteAsync(request.Version, ct);
            return success ? Results.Ok() : Results.BadRequest(new { error });
        });

        admin.MapGet("/worker-releases/{version}/download", async (
            string version,
            WorkerReleaseService releases,
            CancellationToken ct) =>
        {
            var (stream, fileName, error) = await releases.OpenPackageAsync(version, ct);
            if (stream is null || fileName is null)
            {
                return Results.NotFound(new { error = error ?? "Релиз не найден." });
            }

            return Results.File(stream, "application/octet-stream", fileName);
        });

        admin.MapGet("/audit", async (
            string? q,
            string? action,
            DateOnly? date,
            int? page,
            int? pageSize,
            PanelAuditService audit,
            CancellationToken ct) =>
            Results.Ok(await audit.SearchAsync(
                q,
                action,
                date,
                page ?? 1,
                pageSize ?? PanelAuditService.DefaultPageSize,
                ct)));

        admin.MapGet("/security/policy", (PasswordPolicyService policy) =>
            Results.Ok(policy.GetPolicy()));

        admin.MapGet("/logs", async (
            string? q,
            string? level,
            string? service,
            DateTime? date,
            int? page,
            int? pageSize,
            ServiceLogsQueryService logs,
            CancellationToken ct) =>
            Results.Ok(await logs.SearchAsync(
                q,
                level,
                service,
                date,
                page ?? 1,
                pageSize ?? ServiceLogsQueryService.DefaultPageSize,
                ct)));

    }
}

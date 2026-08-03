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

public static class WorkerPanelEndpoints
{
    public static void Map(WebApplication app)
    {
        var workerPanel = app.MapGroup("/api/v1/workers").RequireAuthorization("Panel");
        workerPanel.MapGet("/{id:guid}/config", async (
            Guid id,
            WorkerConfigService configService,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var config = await configService.GetConfigForWorkerAsync(id, scope, ct);
            return config is null ? Results.NotFound() : Results.Ok(config);
        });

        workerPanel.MapPatch("/{id:guid}/settings", async (
            Guid id,
            UpdateWorkerSettingsRequest request,
            WorkerConfigService configService,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (config, error) = await configService.UpdateSettingsAsync(id, request, scope, ct);
            if (error is not null)
            {
                return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            return Results.Ok(config);
        });

        workerPanel.MapPost("/{id:guid}/commands", async (
            Guid id,
            WorkerCommandRequest request,
            WorkerCommandService commands,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (success, error) = await commands.EnqueueAsync(id, request.Command, scope, ct);
            if (!success)
            {
                return error is not null && error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            return Results.Ok(new { message = "Команда поставлена в очередь." });
        });

        workerPanel.MapPatch("/{id:guid}/accounts/{accountId:guid}", async (
            Guid id,
            Guid accountId,
            UpdateWorkerAccountRequest request,
            WorkerConfigService configService,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (account, error) = await configService.UpdateAccountEnabledAsync(id, accountId, request, scope, ct);
            if (error is not null)
            {
                return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            return Results.Ok(account);
        });

        workerPanel.MapPut("/{id:guid}/accounts/{accountId:guid}/credentials", async (
            Guid id,
            Guid accountId,
            UpdateWorkerAccountCredentialsRequest request,
            WorkerConfigService configService,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (credentials, error) = await configService.UpdateAccountCredentialsAsync(
                id, accountId, request, scope, ct);
            if (error is not null)
            {
                return error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            return Results.Ok(credentials);
        });

        workerPanel.MapPost("/{id:guid}/accounts/{accountId:guid}/refresh-subprofiles", async (
            Guid id,
            Guid accountId,
            WorkerConfigService configService,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (success, error) = await configService.RequestSubProfilesRefreshAsync(id, accountId, scope, ct);
            if (!success)
            {
                return error is not null && error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            return Results.Ok(new { message = "Запрос на обновление субпрофилей отправлен воркеру." });
        });

        workerPanel.MapPatch("/{id:guid}/accounts/{accountId:guid}/subprofiles/{subProfileId}", async (
            Guid id,
            Guid accountId,
            string subProfileId,
            UpdateWorkerSubProfileRequest request,
            WorkerConfigService configService,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (success, error) = await configService.UpdateSubProfileEnabledAsync(
                id, accountId, subProfileId, request, scope, ct);
            if (!success)
            {
                return error is not null && error.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                    ? Results.NotFound(new { error })
                    : Results.BadRequest(new { error });
            }

            return Results.Ok(new { message = request.IsEnabledInPanel ? "Субпрофиль включён." : "Субпрофиль отключён." });
        });

    }
}

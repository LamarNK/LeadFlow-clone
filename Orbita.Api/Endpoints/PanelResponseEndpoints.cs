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

public static class PanelResponseEndpoints
{
    public static void Map(WebApplication app)
    {
        var panel = app.MapGroup("/api/v1/panel").RequireAuthorization("Panel");
        panel.MapGet("/responses", async (
            ResponsesQueryService responses,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            string? status,
            string? search,
            string? vacancy,
            Guid? workerId,
            Guid? accountId,
            string? bitrixDestination,
            string? gender,
            int? ageFrom,
            int? ageTo,
            Guid? officeId,
            DateTime? from,
            DateTime? to,
            int? page,
            int? pageSize,
            string? sort,
            string? dir,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await responses.GetPageAsync(
                scope,
                officeId,
                status,
                search,
                vacancy,
                workerId,
                accountId,
                bitrixDestination,
                gender,
                ageFrom,
                ageTo,
                from,
                to,
                page ?? 1,
                pageSize ?? 10,
                sort,
                dir,
                ct));
        });

        panel.MapGet("/responses/summary", async (
            ResponsesQueryService responses,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            string? status,
            string? search,
            string? vacancy,
            Guid? workerId,
            Guid? accountId,
            string? bitrixDestination,
            string? gender,
            int? ageFrom,
            int? ageTo,
            Guid? officeId,
            DateTime? from,
            DateTime? to,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await responses.GetSummaryAsync(
                scope,
                officeId,
                status,
                search,
                vacancy,
                workerId,
                accountId,
                bitrixDestination,
                gender,
                ageFrom,
                ageTo,
                from,
                to,
                ct));
        });

        panel.MapGet("/statistics", async (
            OfficeStatisticsQueryService statistics,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            Guid? officeId,
            DateTime? from,
            DateTime? to,
            Guid[]? workerIds,
            Guid[]? accountIds,
            string? vacancy,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await statistics.GetStatisticsAsync(
                scope,
                officeId,
                from,
                to,
                workerIds,
                accountIds,
                vacancy,
                ct));
        });

        panel.MapGet("/responses/filters/accounts", async (
            ResponsesQueryService responses,
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

            return Results.Ok(await responses.GetFilterAccountsAsync(scope, officeId, ct));
        });

        panel.MapGet("/responses/filters/vacancies", async (
            ResponsesQueryService responses,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            Guid? officeId,
            DateTime? from,
            DateTime? to,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await responses.GetFilterVacanciesAsync(scope, officeId, from, to, ct));
        });

        panel.MapGet("/responses/{id:guid}", async (
            Guid id,
            ResponsesQueryService responses,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var detail = await responses.GetDetailAsync(id, scope, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        panel.MapPost("/responses/{id:guid}/resend-bitrix", async (
            Guid id,
            CandidateIngestionService ingestion,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await ingestion.ResendToBitrixAsync(id, scope, ct));
        });

        panel.MapGet("/bitrix-instances", async (
            BitrixInstanceService bitrixInstances,
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

            return Results.Ok(await bitrixInstances.ListAsync(scope, officeId, ct));
        });

        panel.MapPost("/bitrix-instances", async (
            CreateBitrixInstanceRequest request,
            BitrixInstanceService bitrixInstances,
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

            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var (instance, error) = await bitrixInstances.CreateAsync(scope, officeId, request, actorUserId, ct);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(instance);
        });

        panel.MapGet("/bitrix-instances/{id:guid}", async (
            Guid id,
            BitrixInstanceService bitrixInstances,
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

            var instance = await bitrixInstances.GetAsync(id, scope, officeId, ct);
            return instance is null ? Results.NotFound() : Results.Ok(instance);
        });

        panel.MapPut("/bitrix-instances/{id:guid}", async (
            Guid id,
            UpdateBitrixInstanceRequest request,
            BitrixInstanceService bitrixInstances,
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

            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var (instance, error) = await bitrixInstances.UpdateAsync(id, scope, officeId, request, actorUserId, ct);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(instance);
        });

        panel.MapDelete("/bitrix-instances/{id:guid}", async (
            Guid id,
            BitrixInstanceService bitrixInstances,
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

            var (success, error) = await bitrixInstances.DeleteAsync(id, scope, officeId, ct);
            return error is not null ? Results.BadRequest(new { error }) : Results.NoContent();
        });

        panel.MapPost("/bitrix-instances/validate-webhook", async (
            ValidateBitrixInstanceRequest request,
            BitrixWebhookValidator validator,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var validation = await validator.ValidateAsync(request.WebhookUrl, ct);
            return Results.Ok(validation);
        });

        panel.MapPost("/bitrix-instances/{id:guid}/validate", async (
            Guid id,
            ValidateBitrixInstanceRequest request,
            BitrixInstanceService bitrixInstances,
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

            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var (validation, error) = await bitrixInstances.ValidateAsync(
                id,
                scope,
                officeId,
                request.WebhookUrl,
                actorUserId,
                persistResult: true,
                ct);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(validation);
        });

        panel.MapGet("/distribution-route", async (
            DistributionRouteService distributionRoute,
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

            var route = await distributionRoute.GetAsync(scope, officeId, ct);
            return route is null ? Results.BadRequest(new { error = "Офис не назначен." }) : Results.Ok(route);
        });

        panel.MapPut("/distribution-route", async (
            SaveDistributionRouteRequest request,
            DistributionRouteService distributionRoute,
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

            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var (route, error) = await distributionRoute.SaveAsync(scope, officeId, request, actorUserId, ct);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(route);
        });

        panel.MapPost("/responses/{id:guid}/send-bitrix", async (
            Guid id,
            SendResponseToBitrixRequest request,
            ManualBitrixSendService manualSend,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await manualSend.SendAsync(id, request.BitrixInstanceId, scope, ct));
        });

        // Multi-channel delivery: CRM and/or Bitrix (Bitrix is temporary/legacy).
        panel.MapPost("/responses/{id:guid}/deliver", async (
            Guid id,
            DeliverResponseRequest request,
            ResponseDeliveryService delivery,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            return Results.Ok(await delivery.DeliverAsync(id, request, scope, DistributionModes.Manual, ct));
        });

        panel.MapPost("/responses/deliver-bulk", async (
            BulkDeliverResponsesRequest request,
            ResponseDeliveryService delivery,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (result, error) = await delivery.DeliverBulkAsync(request, scope, ct);
            return error is not null
                ? Results.BadRequest(new { error })
                : Results.Ok(result);
        });

        panel.MapPost("/responses/send-bitrix-bulk", async (
            BulkSendResponsesToBitrixRequest request,
            BulkResponsesBitrixSendService bulkSend,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (result, error) = await bulkSend.SendAsync(request.ResponseIds, request.BitrixInstanceId, scope, ct);
            return error is not null
                ? Results.BadRequest(new { error })
                : Results.Ok(result);
        });

        panel.MapPut("/me/integrations/bitrix", async (
            SaveBitrixIntegrationRequest request,
            OfficeBitrixIntegrationService officeBitrix,
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
            var (integration, error) = await officeBitrix.SaveAsync(
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


        panel.MapPost("/me/integrations/bitrix/validate", async (
            ValidateBitrixIntegrationRequest request,
            OfficeBitrixIntegrationService officeBitrix,
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
            var (validation, error) = await officeBitrix.ValidateAsync(
                officeId,
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

    }
}

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
        var responses = app.MapGroup("/api/v1/panel").RequireAuthorization(PanelPermissions.Responses);
        var statistics = app.MapGroup("/api/v1/panel").RequireAuthorization(PanelPermissions.Statistics);
        var settings = app.MapGroup("/api/v1/panel").RequireAuthorization(PanelPermissions.Settings);

        responses.MapGet("/responses", async (
            ResponsesQueryService responses,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            string? status,
            string? search,
            string? vacancy,
            Guid? workerId,
            Guid[]? workerIds,
            Guid? accountId,
            Guid[]? accountIds,
            string[]? bitrixDestination,
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
                bitrixDestination: null,
                gender,
                ageFrom,
                ageTo,
                from,
                to,
                page ?? 1,
                pageSize ?? 10,
                sort,
                dir,
                ct,
                workerIds,
                accountIds,
                bitrixDestination));
        });

        responses.MapGet("/responses/summary", async (
            ResponsesQueryService responses,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            string? status,
            string? search,
            string? vacancy,
            Guid? workerId,
            Guid[]? workerIds,
            Guid? accountId,
            Guid[]? accountIds,
            string[]? bitrixDestination,
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
                bitrixDestination: null,
                gender,
                ageFrom,
                ageTo,
                from,
                to,
                ct,
                workerIds,
                accountIds,
                bitrixDestination));
        });

        statistics.MapGet("/statistics", async (
            OfficeStatisticsQueryService statistics,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            Guid? officeId,
            DateTime? from,
            DateTime? to,
            Guid[]? workerIds,
            Guid[]? accountIds,
            string? vacancy,
            int? tz,
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
                timeZoneOffsetMinutes: tz,
                ct));
        });

        responses.MapGet("/responses/filters/accounts", async (
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

        responses.MapGet("/responses/filters/vacancies", async (
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

        responses.MapGet("/responses/{id:guid}", async (
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

        responses.MapPut("/responses/{id:guid}", async (
            Guid id,
            UpdateResponseRequest request,
            ResponseEditService responseEdit,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var result = await responseEdit.UpdateAsync(id, request, scope, ct);
            return result.Success
                ? Results.Ok(result)
                : Results.BadRequest(new { error = result.ErrorMessage ?? "Не удалось сохранить отклик." });
        });

        responses.MapGet("/responses/{id:guid}/avatar", async (
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

            var avatar = await responses.GetAvatarAsync(id, scope, ct);
            return avatar is null
                ? Results.NotFound()
                : Results.File(avatar.Bytes, avatar.ContentType);
        });

        responses.MapPost("/responses/{id:guid}/resend-bitrix", async (
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

        settings.MapGet("/bitrix-instances", async (
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

        // Office-scoped write: operators manage Bitrix for their office in My Settings.
        // Authorization is Settings permission + OfficeScope (same as distribution-route).
        settings.MapPost("/bitrix-instances", async (
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

        settings.MapGet("/bitrix-instances/{id:guid}", async (
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

        settings.MapPut("/bitrix-instances/{id:guid}", async (
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

        settings.MapDelete("/bitrix-instances/{id:guid}", async (
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

        settings.MapPost("/bitrix-instances/validate-webhook", async (
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

        settings.MapPost("/bitrix-instances/{id:guid}/validate", async (
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

        settings.MapPost("/bitrix-instances/{id:guid}/crm-import/preview", async (
            Guid id,
            BitrixCrmImportPreviewRequest request,
            BitrixCrmImportService crmImport,
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

            var (preview, error) = await crmImport.PreviewAsync(id, scope, officeId, request, ct);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(preview);
        });

        settings.MapPost("/bitrix-instances/{id:guid}/crm-import/file/preview", async (
            Guid id,
            HttpRequest request,
            BitrixCrmImportService crmImport,
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

            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Ожидается файл выгрузки Bitrix24." });
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "Выберите файл выгрузки Bitrix24." });
            }

            _ = int.TryParse(form["categoryId"].FirstOrDefault(), out var categoryId);
            var stageNames = form["stageNames"]
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList();
            await using var stream = file.OpenReadStream();
            var (preview, error) = await crmImport.PreviewFileAsync(
                id,
                scope,
                officeId,
                Math.Max(0, categoryId),
                stageNames,
                stream,
                file.FileName,
                ct);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(preview);
        });

        settings.MapPost("/bitrix-instances/{id:guid}/crm-import", async (
            Guid id,
            BitrixCrmImportExecuteRequest request,
            BitrixCrmImportService crmImport,
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
            var (result, error) = await crmImport.ImportAsync(id, scope, officeId, request, actorUserId, ct);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(result);
        });

        settings.MapPost("/bitrix-instances/{id:guid}/crm-import/file", async (
            Guid id,
            BitrixCrmFileImportExecuteRequest request,
            BitrixCrmImportService crmImport,
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
            var (result, error) = await crmImport.ImportFileAsync(id, scope, officeId, request, actorUserId, ct);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(result);
        });

        settings.MapGet("/distribution-route", async (
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

        settings.MapPut("/distribution-route", async (
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

        responses.MapPost("/responses/{id:guid}/send-bitrix", async (
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
        responses.MapPost("/responses/{id:guid}/deliver", async (
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

        responses.MapPost("/responses/deliver-bulk", async (
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

        responses.MapPost("/responses/send-bitrix-bulk", async (
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

        settings.MapPut("/me/integrations/bitrix", async (
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


        settings.MapPost("/me/integrations/bitrix/validate", async (
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

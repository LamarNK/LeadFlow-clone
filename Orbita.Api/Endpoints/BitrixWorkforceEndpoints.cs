using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Api.Endpoints;

public static class BitrixWorkforceEndpoints
{
    internal const string RateLimitPolicyName = "bitrix-workforce-webhook";
    private const long MaxWebhookBodyBytes = 256 * 1024;

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/integrations/bitrix/events/{publicId:guid}", async (
            Guid publicId,
            HttpRequest request,
            BitrixWorkforceEventReceiver receiver,
            CancellationToken ct) =>
        {
            if (request.ContentLength is > MaxWebhookBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var incoming = await BitrixWorkforceEventParser.ParseAsync(request, ct);
            if (incoming is null)
            {
                return Results.BadRequest(new { error = "Invalid Bitrix24 event payload." });
            }

            var result = await receiver.ReceiveAsync(publicId, incoming, ct);
            return result.Outcome switch
            {
                BitrixWorkforceReceiveOutcome.Accepted => Results.Accepted(value: new { status = "accepted" }),
                BitrixWorkforceReceiveOutcome.Duplicate => Results.Ok(new { status = "duplicate" }),
                BitrixWorkforceReceiveOutcome.Ignored => Results.Ok(new { status = "ignored" }),
                BitrixWorkforceReceiveOutcome.Unauthorized => Results.Unauthorized(),
                _ => Results.BadRequest(new { error = result.Message })
            };
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicyName)
        .WithMetadata(new RequestSizeLimitAttribute(MaxWebhookBodyBytes));

        var panel = app.MapGroup("/api/v1/panel").RequireAuthorization("Panel");

        panel.MapGet("/bitrix-instances/{id:guid}/workforce", async (
            Guid id,
            BitrixWorkforceSettingsService workforce,
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

            var settings = await workforce.GetAsync(id, scope, officeId, ct);
            return settings is null ? Results.NotFound() : Results.Ok(settings);
        });

        panel.MapPut("/bitrix-instances/{id:guid}/workforce", async (
            Guid id,
            UpdateBitrixWorkforceSettingsRequest request,
            BitrixWorkforceSettingsService workforce,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            Guid? officeId,
            CancellationToken ct) =>
        {
            if (!principal.IsInRole(PanelRoles.Admin))
            {
                return Results.Forbid();
            }

            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var (settings, error) = await workforce.SaveAsync(
                id,
                scope,
                officeId,
                request,
                actorUserId,
                ct);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(settings);
        });

        panel.MapGet("/bitrix-instances/{id:guid}/workforce/assignments", async (
            Guid id,
            BitrixWorkforceSettingsService workforce,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            Guid? officeId,
            int? take,
            CancellationToken ct) =>
        {
            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var assignments = await workforce.GetRecentAssignmentsAsync(
                id,
                scope,
                officeId,
                take ?? 100,
                ct);
            return assignments is null ? Results.NotFound() : Results.Ok(assignments);
        });

        panel.MapPut("/bitrix-instances/{id:guid}/workforce/receiver", async (
            Guid id,
            ConfigureBitrixWorkforceReceiverRequest request,
            BitrixWorkforceSettingsService workforce,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            Guid? officeId,
            CancellationToken ct) =>
        {
            if (!principal.IsInRole(PanelRoles.Admin))
            {
                return Results.Forbid();
            }

            var scope = await officeScope.ResolveAsync(principal, ct);
            if (!scope.HasAccess)
            {
                return Results.Forbid();
            }

            var (receiver, error) = await workforce.ConfigureReceiverAsync(
                id,
                scope,
                officeId,
                request,
                ct);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(receiver);
        });
    }
}

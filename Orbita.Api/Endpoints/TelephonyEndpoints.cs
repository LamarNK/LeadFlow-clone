using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Api.Endpoints;

public static class TelephonyEndpoints
{
    private const long MaxWebhookBodyBytes = 64 * 1024;

    public static void Map(WebApplication app)
    {
        app.MapMethods(
            "/api/v1/integrations/telephony/sipout/{publicId:guid}",
            [HttpMethods.Get, HttpMethods.Post],
            async Task<IResult> (
                Guid publicId,
                HttpRequest request,
                CrmTelephonyService telephony,
                CancellationToken ct) =>
            {
                if (request.ContentLength is > MaxWebhookBodyBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                var values = await ReadValuesAsync(request, ct);
                var payload = new SipoutCallWebhookPayload(
                    Get(values, "C_ID", "CALL_ID", "call_id") ?? string.Empty,
                    Get(values, "CID", "caller", "caller_phone"),
                    Get(values, "DID", "called", "called_phone"),
                    Get(values, "C_TYPE", "direction", "call_type"),
                    Get(values, "PREV_EXTEN", "EXTEN", "extension", "sip_login"),
                    Get(values, "LAST_CALLER", "last_caller"),
                    Get(values, "C_START", "started_at", "start_time"),
                    Get(values, "C_TIME", "duration", "duration_seconds"),
                    Get(values, "LAST_RECORDING_URL", "recording_url", "record_url"));
                var result = await telephony.ReceiveSipoutCallAsync(
                    publicId,
                    Get(values, "secret"),
                    payload,
                    ct);
                return result.Outcome switch
                {
                    SipoutCallReceiveOutcome.Accepted => Results.Accepted(value: new { status = "accepted", result.CallId, result.CardId }),
                    SipoutCallReceiveOutcome.Updated => Results.Ok(new { status = "updated", result.CallId, result.CardId }),
                    SipoutCallReceiveOutcome.Unmatched => Results.Accepted(value: new { status = "unmatched", result.CallId }),
                    SipoutCallReceiveOutcome.Unauthorized => Results.Unauthorized(),
                    _ => Results.BadRequest(new { error = result.Message ?? "Invalid SIPOUT call payload." })
                };
            })
            .AllowAnonymous()
            .RequireRateLimiting(BitrixWorkforceEndpoints.RateLimitPolicyName)
            .WithMetadata(new RequestSizeLimitAttribute(MaxWebhookBodyBytes));

        var admin = app.MapGroup("/api/v1/crm/telephony")
            .RequireAuthorization(PanelPermissions.Administration);

        admin.MapGet("/offices/{officeId:guid}/sipout", async (
            Guid officeId,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanAccessOfficeAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            var settings = await telephony.GetSettingsAsync(officeId, ct);
            return settings is null ? Results.NotFound() : Results.Ok(settings);
        });

        admin.MapPost("/offices/{officeId:guid}/sipout/receiver", async (
            Guid officeId,
            HttpRequest request,
            IOptions<BitrixWorkforceOptions> publicEndpointOptions,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanAccessOfficeAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            // The Web application normally calls this API over Docker's internal address.
            // Generate the provider callback from the configured public API URL instead.
            var publicBaseUrl = ResolvePublicBaseUrl(publicEndpointOptions.Value.PublicBaseUrl, request);
            var (receiver, error) = await telephony.RotateReceiverAsync(officeId, publicBaseUrl, ct);
            return error is null ? Results.Ok(receiver) : Results.BadRequest(new { error });
        });

        admin.MapPut("/offices/{officeId:guid}/sipout/enabled", async (
            Guid officeId,
            UpdateCrmTelephonyEnabledRequest request,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanAccessOfficeAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            return await telephony.SetEnabledAsync(officeId, request.IsEnabled, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        admin.MapPut("/offices/{officeId:guid}/sipout/bindings", async (
            Guid officeId,
            UpdateCrmTelephonyBindingRequest request,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanAccessOfficeAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            var (binding, error) = await telephony.SetBindingAsync(
                officeId, request.UserId, request.ProviderUserKey, ct);
            return error is null ? Results.Ok(binding) : Results.BadRequest(new { error });
        });

        admin.MapDelete("/offices/{officeId:guid}/sipout/bindings/{userId}", async (
            Guid officeId,
            string userId,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanAccessOfficeAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            return await telephony.RemoveBindingAsync(officeId, userId, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }

    private static async Task<bool> CanAccessOfficeAsync(
        Guid officeId,
        OfficeScopeService officeScope,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (!PanelRoles.HasElevatedOfficeAccess(principal))
        {
            return false;
        }
        var scope = await officeScope.ResolveAsync(principal, ct);
        return scope.CanAccessOffice(officeId);
    }

    private static async Task<Dictionary<string, string>> ReadValuesAsync(
        HttpRequest request,
        CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Query)
        {
            values[pair.Key] = pair.Value.ToString();
        }

        if (request.Method == HttpMethods.Post && request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            foreach (var pair in form)
            {
                values[pair.Key] = pair.Value.ToString();
            }
        }
        else if (request.Method == HttpMethods.Post
            && request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var json = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(request.Body, cancellationToken: ct);
            if (json is not null)
            {
                foreach (var pair in json)
                {
                    values[pair.Key] = pair.Value.ValueKind == JsonValueKind.String
                        ? pair.Value.GetString() ?? string.Empty
                        : pair.Value.ToString();
                }
            }
        }
        return values;
    }

    private static string? Get(IReadOnlyDictionary<string, string> values, params string[] names)
    {
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private static string ResolvePublicBaseUrl(string? configuredValue, HttpRequest request)
    {
        var configured = configuredValue?.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured)
            && Uri.TryCreate(configured, UriKind.Absolute, out var publicUri)
            && (publicUri.Scheme == Uri.UriSchemeHttps || publicUri.Scheme == Uri.UriSchemeHttp))
        {
            return configured;
        }

        return $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
    }
}

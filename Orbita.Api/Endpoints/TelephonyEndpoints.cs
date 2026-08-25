using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Api.Endpoints;

public static class TelephonyEndpoints
{
    private const long MaxWebhookBodyBytes = 64 * 1024;
    private const long MaxAsteriskWebhookBodyBytes = 105L * 1024 * 1024;

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

        app.MapPost(
            "/api/v1/integrations/telephony/asterisk/{publicId:guid}",
            async Task<IResult> (
                Guid publicId,
                HttpRequest request,
                CrmTelephonyService telephony,
                CancellationToken ct) =>
            {
                if (!request.HasFormContentType)
                {
                    return Results.BadRequest(new { error = "A multipart Asterisk event is required." });
                }
                if (request.ContentLength is > MaxAsteriskWebhookBodyBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                var form = await request.ReadFormAsync(ct);
                var recording = form.Files.GetFile("recording");

                var payload = new AsteriskCallWebhookPayload(
                    form["external_call_id"].ToString(),
                    form["caller_phone"].ToString(),
                    form["called_phone"].ToString(),
                    form["direction"].ToString(),
                    form["internal"].ToString(),
                    form["started_at"].ToString(),
                    form["duration_seconds"].ToString());
                var secret = request.Headers.TryGetValue("X-Orbita-Webhook-Secret", out var secretHeader)
                    ? secretHeader.ToString()
                    : null;
                await using var recordingStream = recording?.OpenReadStream();
                var result = await telephony.ReceiveAsteriskCallAsync(
                    publicId,
                    secret,
                    payload,
                    recordingStream,
                    recording?.Length ?? 0,
                    recording?.FileName,
                    recording?.ContentType,
                    ct);
                return ToReceiveResult(result, "Invalid Asterisk call payload.");
            })
            .AllowAnonymous()
            .RequireRateLimiting(BitrixWorkforceEndpoints.RateLimitPolicyName)
            .WithMetadata(
                new RequestSizeLimitAttribute(MaxAsteriskWebhookBodyBytes),
                new RequestFormLimitsAttribute
                {
                    MultipartBodyLengthLimit = MaxAsteriskWebhookBodyBytes,
                    ValueLengthLimit = 4096,
                    MultipartHeadersLengthLimit = 16 * 1024
                });

        app.MapGet(
            "/api/v1/integrations/telephony/asterisk/{publicId:guid}/route",
            async Task<IResult> (
                Guid publicId,
                string? caller,
                string? called,
                HttpRequest request,
                CrmTelephonyService telephony,
                CancellationToken ct) =>
            {
                var secret = request.Headers.TryGetValue("X-Orbita-Webhook-Secret", out var secretHeader)
                    ? secretHeader.ToString()
                    : null;
                var result = await telephony.ResolveAsteriskInboundRouteAsync(
                    publicId,
                    secret,
                    caller,
                    called,
                    ct);
                if (result.Outcome == AsteriskInboundRouteOutcome.Unauthorized)
                {
                    return Results.Unauthorized();
                }
                if (result.Outcome == AsteriskInboundRouteOutcome.Invalid)
                {
                    return Results.BadRequest(new { error = result.Message ?? "Invalid inbound route request." });
                }

                var preferred = result.PreferredExtension ?? string.Empty;
                var fallback = string.Join(
                    '&',
                    (result.FallbackExtensions ?? [])
                        .Select(extension => $"PJSIP/{extension}-webrtc"));
                return Results.Text($"{preferred}^{fallback}", "text/plain");
            })
            .AllowAnonymous()
            .RequireRateLimiting(BitrixWorkforceEndpoints.RateLimitPolicyName);

        app.MapPost(
            "/api/v1/integrations/telephony/plusofon/{publicId:guid}",
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
                var hookEvent = Get(values, "hook_event", "event");
                if (!string.IsNullOrWhiteSpace(hookEvent)
                    && !string.Equals(hookEvent, "destroy", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Accepted(value: new { status = "ignored", reason = "not-final" });
                }

                var payload = new PlusofonCallWebhookPayload(
                    Get(values, "call_id", "id") ?? string.Empty,
                    Get(values, "from", "caller", "caller_phone"),
                    Get(values, "to", "called", "called_phone"),
                    Get(values, "direction", "type_call"),
                    Get(values, "internal", "extension", "sip_login"),
                    Get(values, "timestamp", "started_at", "start_time"),
                    Get(values, "duration", "duration_seconds"),
                    Get(values, "record", "recording_url", "record_url"));
                var secret = request.Headers.TryGetValue("X-Orbita-Webhook-Secret", out var secretHeader)
                    ? secretHeader.ToString()
                    : Get(values, "secret");
                var result = await telephony.ReceivePlusofonCallAsync(publicId, secret, payload, ct);
                return ToReceiveResult(result, "Invalid Plusofon call payload.");
            })
            .AllowAnonymous()
            .RequireRateLimiting(BitrixWorkforceEndpoints.RateLimitPolicyName)
            .WithMetadata(new RequestSizeLimitAttribute(MaxWebhookBodyBytes));

        app.MapGet(
            "/api/v1/crm/telephony/webrtc/config",
            async Task<IResult> (
                Guid? officeId,
                ClaimsPrincipal principal,
                HttpResponse response,
                OfficeScopeService officeScope,
                CrmTelephonyService telephony,
                IOptions<CrmTelephonyWebRtcOptions> configuredOptions,
                TimeProvider timeProvider,
                CancellationToken ct) =>
            {
                var options = configuredOptions.Value;
                if (!options.Enabled)
                {
                    return Results.NotFound(new { error = "Браузерная телефония не включена." });
                }

                var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                             ?? principal.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Results.Unauthorized();
                }

                var scope = await officeScope.ResolveAsync(principal, ct);
                var effectiveOfficeId = scope.ResolveFilter(officeId);
                if (effectiveOfficeId is not Guid resolvedOfficeId
                    || !scope.CanAccessOffice(resolvedOfficeId))
                {
                    return Results.Forbid();
                }

                var (endpoint, endpointError) = await telephony.GetOrProvisionWebRtcEndpointAsync(
                    resolvedOfficeId,
                    userId,
                    ct);
                if (endpoint is null
                    || string.IsNullOrWhiteSpace(options.WebSocketUrl)
                    || string.IsNullOrWhiteSpace(options.SipDomain))
                {
                    return Results.NotFound(new
                    {
                        error = endpointError ?? "Для сотрудника не настроена браузерная SIP-линия."
                    });
                }

                if (!Uri.TryCreate(options.WebSocketUrl, UriKind.Absolute, out var socketUri)
                    || socketUri.Scheme is not ("ws" or "wss"))
                {
                    return Results.Problem(
                        "Некорректный адрес WebSocket для телефонии.",
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                var iceServerUrls = ParseIceServerUrls(options.IceServerUrls);
                var hasTurnServer = iceServerUrls.Any(url =>
                    url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase));
                var iceUsername = string.IsNullOrWhiteSpace(options.IceUsername)
                    ? null
                    : options.IceUsername.Trim();
                var iceCredential = string.IsNullOrWhiteSpace(options.IceCredential)
                    ? null
                    : options.IceCredential;
                if (hasTurnServer && !string.IsNullOrWhiteSpace(options.IceAuthSecret))
                {
                    (iceUsername, iceCredential) = CrmTelephonyIceCredentialFactory.Create(
                        options.IceAuthSecret,
                        endpoint.Extension,
                        timeProvider.GetUtcNow(),
                        options.IceCredentialTtlSeconds);
                }
                else if (hasTurnServer
                    && (string.IsNullOrWhiteSpace(iceUsername)
                        || string.IsNullOrWhiteSpace(iceCredential)))
                {
                    return Results.Problem(
                        "Для TURN-сервера не настроен временный секрет или статические реквизиты.",
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                response.Headers.CacheControl = "no-store, no-cache, max-age=0";
                response.Headers.Pragma = "no-cache";
                return Results.Ok(new CrmTelephonyWebRtcConfigDto(
                    options.WebSocketUrl.Trim(),
                    $"sip:{endpoint.AuthorizationUsername.Trim()}@{options.SipDomain.Trim()}",
                    options.SipDomain.Trim(),
                    endpoint.Extension,
                    endpoint.AuthorizationUsername.Trim(),
                    endpoint.Password,
                    iceServerUrls,
                    iceUsername,
                    iceCredential));
            })
            .RequireAuthorization(PanelPermissions.CrmBoard);

        var admin = app.MapGroup("/api/v1/crm/telephony")
            .RequireAuthorization("OfficeStaff");

        admin.MapGet("/offices/{officeId:guid}/{provider}", async (
            Guid officeId,
            string provider,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanAccessOfficeAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            if (!CrmTelephonyProviders.IsSupported(provider)) return Results.BadRequest();
            var settings = await telephony.GetSettingsAsync(officeId, ct, provider);
            return settings is null ? Results.NotFound() : Results.Ok(settings);
        });

        admin.MapPost("/offices/{officeId:guid}/{provider}/receiver", async (
            Guid officeId,
            string provider,
            HttpRequest request,
            IOptions<BitrixWorkforceOptions> publicEndpointOptions,
            IOptions<TelephonyGatewayPublicOptions> telephonyGatewayOptions,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanManageTelephonyAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            if (!CrmTelephonyProviders.IsSupported(provider)) return Results.BadRequest();
            // The Web application normally calls this API over Docker's internal address.
            // Generate the provider callback from the configured public API URL instead.
            var publicBaseUrl = ResolvePublicBaseUrl(
                telephonyGatewayOptions.Value.PublicBaseUrl,
                publicEndpointOptions.Value.PublicBaseUrl,
                request);
            var (receiver, error) = await telephony.RotateReceiverAsync(officeId, publicBaseUrl, ct, provider);
            return error is null ? Results.Ok(receiver) : Results.BadRequest(new { error });
        });

        admin.MapPut("/offices/{officeId:guid}/{provider}/enabled", async (
            Guid officeId,
            string provider,
            UpdateCrmTelephonyEnabledRequest request,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanManageTelephonyAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            if (!CrmTelephonyProviders.IsSupported(provider)) return Results.BadRequest();
            return await telephony.SetEnabledAsync(officeId, request.IsEnabled, ct, provider)
                ? Results.NoContent()
                : Results.NotFound();
        });

        admin.MapPut("/offices/{officeId:guid}/{provider}/credentials", async (
            Guid officeId,
            string provider,
            UpdatePlusofonCredentialsRequest request,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanManageTelephonyAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            if (!string.Equals(provider, CrmTelephonyProviders.Plusofon, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Реквизиты API поддерживаются только для Плюсофона." });
            }
            var (success, error) = await telephony.SetPlusofonCredentialsAsync(
                officeId,
                request.ClientId,
                request.AccessToken,
                ct);
            return success ? Results.NoContent() : Results.BadRequest(new { error });
        });

        admin.MapPut("/offices/{officeId:guid}/{provider}/sip-account", async (
            Guid officeId,
            string provider,
            UpdateSipProviderAccountRequest request,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanManageTelephonyAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            provider = CrmTelephonyProviders.Normalize(provider);
            if (provider is not (CrmTelephonyProviders.Beeline or CrmTelephonyProviders.Plusofon))
            {
                return Results.BadRequest(new { error = "Форма SIP-аккаунта поддерживает Плюсофон и Билайн." });
            }
            var (success, error) = await telephony.SetSipProviderAccountAsync(officeId, provider, request, ct);
            return success ? Results.NoContent() : Results.BadRequest(new { error });
        });

        admin.MapPost("/offices/{officeId:guid}/{provider}/sip-accounts", async (
            Guid officeId,
            string provider,
            UpdateSipProviderAccountRequest request,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanManageTelephonyAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            if (!string.Equals(provider, CrmTelephonyProviders.Beeline, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Несколько SIP-линий пока поддерживаются только для Билайна." });
            }
            var (success, error, accountKey) = await telephony.UpsertBeelineSipAccountAsync(officeId, null, request, ct);
            return success ? Results.Ok(new { accountKey }) : Results.BadRequest(new { error });
        });

        admin.MapPut("/offices/{officeId:guid}/{provider}/sip-accounts/{accountKey}", async (
            Guid officeId,
            string provider,
            string accountKey,
            UpdateSipProviderAccountRequest request,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanManageTelephonyAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            if (!string.Equals(provider, CrmTelephonyProviders.Beeline, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Несколько SIP-линий пока поддерживаются только для Билайна." });
            }
            var (success, error, _) = await telephony.UpsertBeelineSipAccountAsync(officeId, accountKey, request, ct);
            return success ? Results.NoContent() : Results.BadRequest(new { error });
        });

        admin.MapDelete("/offices/{officeId:guid}/{provider}/sip-accounts/{accountKey}", async (
            Guid officeId,
            string provider,
            string accountKey,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanManageTelephonyAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            if (!string.Equals(provider, CrmTelephonyProviders.Beeline, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest();
            }
            var (success, error) = await telephony.DeleteBeelineSipAccountAsync(officeId, accountKey, ct);
            return success ? Results.NoContent() : Results.BadRequest(new { error });
        });

        admin.MapPut("/offices/{officeId:guid}/{provider}/bindings", async (
            Guid officeId,
            string provider,
            UpdateCrmTelephonyBindingRequest request,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanManageTelephonyAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            if (!CrmTelephonyProviders.IsSupported(provider)) return Results.BadRequest();
            var (binding, error) = await telephony.SetBindingAsync(
                officeId, request.UserId, request.ProviderUserKey, ct, provider, request.OutboundProvider);
            return error is null ? Results.Ok(binding) : Results.BadRequest(new { error });
        });

        admin.MapDelete("/offices/{officeId:guid}/{provider}/bindings/{userId}", async (
            Guid officeId,
            string provider,
            string userId,
            CrmTelephonyService telephony,
            OfficeScopeService officeScope,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await CanManageTelephonyAsync(officeId, officeScope, principal, ct))
            {
                return Results.Forbid();
            }
            if (!CrmTelephonyProviders.IsSupported(provider)) return Results.BadRequest();
            return await telephony.RemoveBindingAsync(officeId, userId, ct, provider)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }

    private static IReadOnlyList<string> ParseIceServerUrls(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(url => url.Length <= 512
                && !url.Any(char.IsWhiteSpace)
                && (url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("stuns:", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static IResult ToReceiveResult(SipoutCallReceiveResult result, string invalidMessage) =>
        result.Outcome switch
        {
            SipoutCallReceiveOutcome.Accepted => Results.Accepted(value: new { status = "accepted", result.CallId, result.CardId }),
            SipoutCallReceiveOutcome.Updated => Results.Ok(new { status = "updated", result.CallId, result.CardId }),
            SipoutCallReceiveOutcome.Unmatched => Results.Accepted(value: new { status = "unmatched", result.CallId }),
            SipoutCallReceiveOutcome.Unauthorized => Results.Unauthorized(),
            _ => Results.BadRequest(new { error = result.Message ?? invalidMessage })
        };

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

    private static async Task<bool> CanManageTelephonyAsync(
        Guid officeId,
        OfficeScopeService officeScope,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (!principal.IsInRole(PanelRoles.Admin) && !principal.IsInRole(PanelRoles.OfficeLead))
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

    private static string ResolvePublicBaseUrl(
        string? telephonyGatewayValue,
        string? fallbackValue,
        HttpRequest request)
    {
        foreach (var value in new[] { telephonyGatewayValue, fallbackValue })
        {
            var configured = value?.Trim().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(configured)
                && Uri.TryCreate(configured, UriKind.Absolute, out var publicUri)
                && (publicUri.Scheme == Uri.UriSchemeHttps || publicUri.Scheme == Uri.UriSchemeHttp))
            {
                return configured;
            }
        }

        return $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
    }
}

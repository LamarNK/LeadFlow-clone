using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Logging.Audit;

namespace Orbita.Api.Auth;

public sealed class WorkerApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    OrbitaDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "WorkerApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var (resolved, apiKey, failureReason, failureMessage) = TryResolveApiKey();
        if (!resolved)
        {
            if (failureReason is not null)
            {
                await LogAuthFailureAsync(failureReason, failureMessage!).ConfigureAwait(false);
                return AuthenticateResult.Fail(failureMessage!);
            }

            return AuthenticateResult.NoResult();
        }

        var hash = ApiKeyService.HashApiKey(apiKey!);
        var worker = await db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.ApiKeyHash == hash).ConfigureAwait(false);
        if (worker is null)
        {
            var source = Request.Headers.ContainsKey("Authorization") ? "authorization_header" : "access_token_query";
            await LogAuthFailureAsync(
                "invalid_api_key",
                "Worker auth failed: invalid API key.",
                new Dictionary<string, object?>
                {
                    ["auth.source"] = source,
                    ["auth.keyLength"] = apiKey!.Length
                }).ConfigureAwait(false);
            return AuthenticateResult.Fail("Invalid API key.");
        }

        if (!worker.IsEnabled)
        {
            await LogAuthFailureAsync("worker_disabled", $"Worker auth failed: worker disabled ({worker.Id}).").ConfigureAwait(false);
            return AuthenticateResult.Fail("Worker is disabled.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, worker.Id.ToString()),
            new Claim(ClaimTypes.Name, worker.DisplayName)
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    private (bool Resolved, string? ApiKey, string? FailureReason, string? FailureMessage) TryResolveApiKey()
    {
        if (Request.Headers.TryGetValue("Authorization", out var header) && header.Count > 0)
        {
            var value = header.ToString();
            if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return (false, null, "invalid_authorization_header", "Worker auth failed: invalid authorization header.");
            }

            var apiKey = value["Bearer ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (false, null, "empty_api_key", "Worker auth failed: API key is empty.");
            }

            // JWT panel tokens also arrive as Bearer on hub negotiate; they are not worker API keys.
            if (apiKey.Contains('.', StringComparison.Ordinal))
            {
                return (false, null, null, null);
            }

            return (true, apiKey, null, null);
        }

        if (!IsHubPath(Request.Path))
        {
            return (false, null, null, null);
        }

        if (!Request.Query.TryGetValue("access_token", out var accessToken))
        {
            return (false, null, null, null);
        }

        var token = accessToken.ToString().Trim();
        if (string.IsNullOrWhiteSpace(token) || token.Contains('.', StringComparison.Ordinal))
        {
            return (false, null, null, null);
        }

        return (true, token, null, null);
    }

    private static bool IsHubPath(PathString path) => WorkerHubPaths.IsWorkerHubPath(path);

    private async Task LogAuthFailureAsync(
        string reason,
        string message,
        Dictionary<string, object?>? extra = null)
    {
        var properties = new Dictionary<string, object?>
        {
            ["http.path"] = Request.Path.Value,
            ["auth.scheme"] = SchemeName
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                properties[key] = value;
            }
        }

        var path = Request.Path.Value ?? "/";
        var detail = $"{message} (path={path})";
        await GlobalLogger.Instance.LogAsync(
            detail,
            DeskLinkAuditLogLevel.Warning,
            errorKey: $"auth.worker.{reason}",
            properties: properties).ConfigureAwait(false);
    }
}
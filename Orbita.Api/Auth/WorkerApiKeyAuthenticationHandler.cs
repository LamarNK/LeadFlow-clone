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
        if (!Request.Headers.TryGetValue("Authorization", out var header) || header.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        var value = header.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await LogAuthFailureAsync("invalid_authorization_header", "Worker auth failed: invalid authorization header.");
            return AuthenticateResult.Fail("Invalid authorization header.");
        }

        var apiKey = value["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await LogAuthFailureAsync("empty_api_key", "Worker auth failed: API key is empty.");
            return AuthenticateResult.Fail("API key is empty.");
        }

        var hash = ApiKeyService.HashApiKey(apiKey);
        var worker = await db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.ApiKeyHash == hash);
        if (worker is null)
        {
            await LogAuthFailureAsync("invalid_api_key", "Worker auth failed: invalid API key.");
            return AuthenticateResult.Fail("Invalid API key.");
        }

        if (!worker.IsEnabled)
        {
            await LogAuthFailureAsync("worker_disabled", $"Worker auth failed: worker disabled ({worker.Id}).");
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

    private async Task LogAuthFailureAsync(string reason, string message)
    {
        await GlobalLogger.Instance.LogAsync(
            message,
            DeskLinkAuditLogLevel.Warning,
            errorKey: $"auth.worker.{reason}",
            properties: new Dictionary<string, object?>
            {
                ["http.path"] = Request.Path.Value,
                ["auth.scheme"] = SchemeName
            });
    }
}
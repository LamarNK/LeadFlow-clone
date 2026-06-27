using Microsoft.Extensions.Options;
using NotifyBot.Application.Options;

namespace NotifyBot.Api.Middleware;

public sealed class PlusofonWebhookAuthMiddleware(
    RequestDelegate next,
    IOptions<PlusofonOptions> options,
    ILogger<PlusofonWebhookAuthMiddleware> logger)
{
    private readonly PlusofonOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsPlusofonWebhook(context.Request))
        {
            await next(context);
            return;
        }

        if (!_options.WebhookValidation)
        {
            await next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Secret))
        {
            logger.LogWarning("Plusofon webhook validation is enabled but secret is empty");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var providedSecret = GetProvidedSecret(context.Request);
        if (!string.Equals(providedSecret, _options.Secret, StringComparison.Ordinal))
        {
            logger.LogWarning("Plusofon webhook rejected due to invalid secret");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }

    private static bool IsPlusofonWebhook(HttpRequest request) =>
        request.Path.StartsWithSegments("/api/webhooks/plusofon", StringComparison.OrdinalIgnoreCase);

    private static string? GetProvidedSecret(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Plusofon-Secret", out var headerValue))
        {
            return headerValue.ToString();
        }

        if (request.Query.TryGetValue("secret", out var queryValue))
        {
            return queryValue.ToString();
        }

        return null;
    }
}
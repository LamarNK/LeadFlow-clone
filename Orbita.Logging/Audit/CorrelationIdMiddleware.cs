using Microsoft.AspNetCore.Http;

namespace Orbita.Logging.Audit;

/// <summary>
/// Пробрасывает correlation id в <see cref="CorrelationContext"/> и заголовок ответа.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = context.TraceIdentifier;
        }

        CorrelationContext.Set(correlationId);
        context.Response.Headers[HeaderName] = correlationId;

        try
        {
            await next(context);
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }
}
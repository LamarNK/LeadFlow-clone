using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Orbita.Logging.Audit;

/// <summary>
/// Пишет в GlobalLogger только значимые HTTP-события: 5xx, auth-ошибки, необработанные исключения.
/// </summary>
public sealed class RequestDiagnosticsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? unhandled = null;

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            unhandled = ex;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            await LogRequestAsync(context, stopwatch.ElapsedMilliseconds, unhandled);
        }
    }

    private static async Task LogRequestAsync(HttpContext context, long elapsedMs, Exception? unhandled)
    {
        if (unhandled is not null)
        {
            await GlobalLogger.Instance.LogAsync(
                $"Unhandled exception {context.Request.Method} {context.Request.Path}{context.Request.QueryString}: {unhandled.Message}",
                DeskLinkAuditLogLevel.Error,
                errorKey: "http.unhandled_exception",
                properties: new Dictionary<string, object?>
                {
                    ["http.method"] = context.Request.Method,
                    ["http.path"] = context.Request.Path.Value,
                    ["http.status"] = context.Response.StatusCode,
                    ["http.elapsed_ms"] = elapsedMs,
                    ["exception.type"] = unhandled.GetType().FullName
                });
            return;
        }

        var statusCode = context.Response.StatusCode;
        if (statusCode >= 500)
        {
            await GlobalLogger.Instance.LogAsync(
                $"HTTP {statusCode} {context.Request.Method} {context.Request.Path}{context.Request.QueryString} {elapsedMs}ms",
                DeskLinkAuditLogLevel.Error,
                errorKey: "http.server_error",
                properties: BuildHttpProperties(context, statusCode, elapsedMs));
            return;
        }

        if (statusCode is 401 or 403 or 429)
        {
            await GlobalLogger.Instance.LogAsync(
                $"HTTP {statusCode} {context.Request.Method} {context.Request.Path}{context.Request.QueryString} {elapsedMs}ms",
                DeskLinkAuditLogLevel.Warning,
                errorKey: $"http.{statusCode}",
                properties: BuildHttpProperties(context, statusCode, elapsedMs));
        }
    }

    private static Dictionary<string, object?> BuildHttpProperties(HttpContext context, int statusCode, long elapsedMs) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["http.method"] = context.Request.Method,
            ["http.path"] = context.Request.Path.Value,
            ["http.status"] = statusCode,
            ["http.elapsed_ms"] = elapsedMs
        };
}
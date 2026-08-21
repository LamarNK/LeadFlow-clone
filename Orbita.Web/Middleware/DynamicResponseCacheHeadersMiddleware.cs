namespace Orbita.Web.Middleware;

/// <summary>
/// Live panel HTML/JSON must not be stored by the browser. Static files are handled
/// separately and already short-circuit before this middleware.
/// </summary>
public sealed class DynamicResponseCacheHeadersMiddleware(RequestDelegate next)
{
    private const string AppliedItemsKey = "__OrbitaDynamicCacheHeadersApplied";

    public async Task Invoke(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            Apply((HttpContext)state!);
            return Task.CompletedTask;
        }, context);

        await next(context);

        if (!context.Response.HasStarted)
        {
            Apply(context);
        }
    }

    private static void Apply(HttpContext httpContext)
    {
        if (httpContext.Items.ContainsKey(AppliedItemsKey))
        {
            return;
        }

        var contentType = httpContext.Response.ContentType;
        if (string.IsNullOrEmpty(contentType))
        {
            return;
        }

        if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            httpContext.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            httpContext.Response.Headers.Pragma = "no-cache";
            httpContext.Items[AppliedItemsKey] = true;
        }
    }
}

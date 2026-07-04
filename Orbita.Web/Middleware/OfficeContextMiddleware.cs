using Orbita.Web.Services;

namespace Orbita.Web.Middleware;

public sealed class OfficeContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IOfficeContext officeContext)
    {
        officeContext.Bind(context);
        await next(context);
    }
}
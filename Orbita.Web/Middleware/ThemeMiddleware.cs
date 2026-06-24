using Orbita.Web.Services;

namespace Orbita.Web.Middleware;

public sealed class ThemeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ThemeService theme)
    {
        theme.LoadFromRequest();
        await next(context);
    }
}
using Microsoft.AspNetCore.Http;
using Orbita.Web.Middleware;

namespace Orbita.Tests;

public sealed class DynamicResponseCacheHeadersMiddlewareTests
{
    [Fact]
    public async Task JsonResponse_IsNotStoredByTheBrowser()
    {
        var middleware = new DynamicResponseCacheHeadersMiddleware(async context =>
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{}");
        });
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        await middleware.Invoke(httpContext);

        Assert.Equal("no-store, no-cache, must-revalidate", httpContext.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-cache", httpContext.Response.Headers.Pragma.ToString());
    }

    [Fact]
    public async Task HtmlResponse_IsNotStoredByTheBrowser()
    {
        var middleware = new DynamicResponseCacheHeadersMiddleware(async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync("<html></html>");
        });
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        await middleware.Invoke(httpContext);

        Assert.Equal("no-store, no-cache, must-revalidate", httpContext.Response.Headers.CacheControl.ToString());
    }
}

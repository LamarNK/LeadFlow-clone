using Microsoft.Extensions.Options;
using Orbita.Web.Auth;
using Orbita.Web.Options;
using Orbita.Web.Services;

namespace Orbita.Web.Middleware;

public sealed class JwtCookieAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        OrbitaAuthService auth,
        IConfiguration config,
        IOptions<DesignPreviewOptions> previewOptions)
    {
        if (context.User.Identity?.IsAuthenticated != true
            && context.Request.Cookies.TryGetValue(AuthSession.TokenCookieName, out var token)
            && !string.IsNullOrWhiteSpace(token))
        {
            if (string.Equals(token, AuthSession.DesignPreviewToken, StringComparison.Ordinal))
            {
                var preview = previewOptions.Value;
                if (preview.Enabled)
                {
                    await auth.SignInPreviewAsync(preview.Email, preview.DisplayName, context.RequestAborted);
                }
                else
                {
                    context.Response.Cookies.Delete(AuthSession.TokenCookieName);
                }
            }
            else if (JwtTokenValidation.TryValidate(token, config, out var principal)
                     && principal is not null
                     && JwtTokenValidation.GetEmail(principal) is { } email)
            {
                await auth.SignInAsync(token, email, context.RequestAborted);
            }
        }

        await next(context);
    }
}
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Orbita.Web.Services;

public sealed class OrbitaAuthService(IHttpContextAccessor httpContextAccessor, AuthSession session)
{
    public async Task SignInAsync(string token, string email, CancellationToken ct = default)
    {
        session.Token = token;
        session.Email = email;

        var context = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("HttpContext is not available.");

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        var identity = new ClaimsIdentity(jwt.Claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = jwt.ValidTo
            });

        context.Response.Cookies.Append(
            AuthSession.TokenCookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = jwt.ValidTo
            });
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        session.Token = null;
        session.Email = null;

        if (httpContextAccessor.HttpContext is { } context)
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Cookies.Delete(AuthSession.TokenCookieName);
        }
    }
}
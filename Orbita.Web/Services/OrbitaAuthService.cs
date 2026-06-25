using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Orbita.Web.Services;

public sealed class OrbitaAuthService(IHttpContextAccessor httpContextAccessor, AuthSession session)
{
    public Task SignInAsync(string token, string email, CancellationToken ct = default)
    {
        if (string.Equals(token, "design-preview", StringComparison.Ordinal))
        {
            return SignInPreviewAsync(email, email, ct);
        }

        return SignInWithJwtAsync(token, email, ct);
    }

    public async Task SignInPreviewAsync(string email, string displayName, CancellationToken ct = default)
    {
        session.Token = "design-preview";
        session.Email = email;

        var context = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("HttpContext is not available.");

        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var claims = new[]
        {
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, displayName),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = expires.UtcDateTime
            });

        context.Response.Cookies.Append(
            AuthSession.TokenCookieName,
            "design-preview",
            new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = expires
            });
    }

    private async Task SignInWithJwtAsync(string token, string email, CancellationToken ct)
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
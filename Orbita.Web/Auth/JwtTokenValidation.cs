using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Orbita.Web.Auth;

public static class JwtTokenValidation
{
    public static bool TryValidate(string token, IConfiguration config, out ClaimsPrincipal? principal)
    {
        principal = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var key = config["Jwt:Key"] ?? "OrbitaDevSigningKey_ChangeInProduction_32chars!";
        var issuer = config["Jwt:Issuer"] ?? "Orbita";
        var audience = config["Jwt:Audience"] ?? "Orbita.Web";

        var handler = new JwtSecurityTokenHandler();
        try
        {
            principal = handler.ValidateToken(
                token,
                new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
                    ClockSkew = TimeSpan.FromMinutes(1)
                },
                out _);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? GetEmail(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Email)
        ?? principal.FindFirstValue(ClaimTypes.Name);
}
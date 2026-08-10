using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Http.Features;
using Orbita.Api.Auth;
using Orbita.Api.Data;
using Orbita.Api.Hubs;
using Orbita.Api.Models;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;
using Orbita.Logging.Audit;

namespace Orbita.Api.Endpoints;

public static class JwtTokenFactory
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan RememberMeLifetime = TimeSpan.FromDays(14);

    public static string CreateToken(
        IdentityUser user,
        IEnumerable<string> roles,
        IEnumerable<string> permissions,
        IConfiguration config,
        Guid? officeId = null,
        bool rememberMe = false) =>
        CreateToken(user, roles, permissions, config, officeId, rememberMe ? RememberMeLifetime : DefaultLifetime);

    public static string CreateToken(
        IdentityUser user,
        IEnumerable<string> roles,
        IEnumerable<string> permissions,
        IConfiguration config,
        Guid? officeId,
        TimeSpan lifetime)
    {
        var key = config["Jwt:Key"] ?? "OrbitaDevSigningKey_ChangeInProduction_32chars!";
        var issuer = config["Jwt:Issuer"] ?? "Orbita";
        var audience = config["Jwt:Audience"] ?? "Orbita.Web";
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new(JwtSecurityStampValidator.SecurityStampClaimType, user.SecurityStamp ?? string.Empty)
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        foreach (var permission in PanelPermissions.Normalize(permissions))
        {
            claims.Add(new Claim(PanelPermissions.ClaimType, permission));
        }

        if (officeId is Guid resolvedOfficeId)
        {
            claims.Add(new Claim(OfficeClaims.OfficeId, resolvedOfficeId.ToString()));
        }

        if (lifetime <= TimeSpan.Zero)
        {
            lifetime = DefaultLifetime;
        }

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer,
            audience,
            claims,
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: credentials);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}

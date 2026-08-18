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

public static class AuthenticationEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/auth/login", async (
            LoginRequest request,
            UserManager<IdentityUser> users,
            SignInManager<IdentityUser> signIn,
            PanelAuditService audit,
            PanelUserService panelUsers,
            AccessProfileService accessProfiles,
            IConfiguration config,
            HttpContext http,
            CancellationToken ct) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString();
            var user = await users.FindByEmailAsync(request.Email);
            if (user is null)
            {
                await audit.LogAsync(null, request.Email, PanelAuditActions.LoginFailed, "user", null, "user_not_found", ip, ct);
                await GlobalLogger.Instance.LogAsync(
                    $"Login failed: user not found ({request.Email}).",
                    DeskLinkAuditLogLevel.Warning,
                    errorKey: "auth.login.user_not_found");
                return Results.Unauthorized();
            }

            if (await users.IsLockedOutAsync(user))
            {
                await audit.LogAsync(user.Id, user.Email, PanelAuditActions.LoginFailed, "user", user.Id, "locked_out", ip, ct);
                await GlobalLogger.Instance.LogAsync(
                    $"Login failed: account locked ({request.Email}).",
                    DeskLinkAuditLogLevel.Warning,
                    errorKey: "auth.login.locked_out");
                return Results.Unauthorized();
            }

            var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: false);
            if (!result.Succeeded)
            {
                await audit.LogAsync(user.Id, user.Email, PanelAuditActions.LoginFailed, "user", user.Id, "invalid_password", ip, ct);
                await GlobalLogger.Instance.LogAsync(
                    $"Login failed: invalid password ({request.Email}).",
                    DeskLinkAuditLogLevel.Warning,
                    errorKey: "auth.login.invalid_password");
                return Results.Unauthorized();
            }

            var roles = await users.GetRolesAsync(user);
            var isGlobalAdmin = roles.Any(r => PanelRoles.IsGlobalAdmin(r));
            Guid? officeId = null;
            if (!isGlobalAdmin)
            {
                officeId = await panelUsers.GetOfficeIdForUserAsync(user.Id, ct);
                if (officeId is null)
                {
                    await audit.LogAsync(user.Id, user.Email, PanelAuditActions.LoginFailed, "user", user.Id, "office_not_assigned", ip, ct);
                    return Results.Json(
                        new { error = "Пользователю не назначен офис. Обратитесь к администратору." },
                        statusCode: StatusCodes.Status403Forbidden);
                }
            }

            var permissions = await panelUsers.GetPermissionOverrideAsync(user)
                ?? await accessProfiles.GetPermissionsForRolesAsync(roles, ct);
            // Always issue a 14-day session so closing the browser/tab does not force re-login.
            var token = JwtTokenFactory.CreateToken(
                user,
                roles,
                permissions,
                config,
                officeId,
                rememberMe: true);
            await panelUsers.RecordActivityAsync(user.Id, ct);
            await audit.LogAsync(user.Id, user.Email, PanelAuditActions.LoginSucceeded, "user", user.Id, null, ip, ct);
            await GlobalLogger.Instance.LogAsync(
                $"Login succeeded ({request.Email}).",
                DeskLinkAuditLogLevel.Info);
            return Results.Ok(new LoginResponse(token, user.Email ?? request.Email));
        });

        app.MapPost("/api/v1/auth/activity", async (
            ClaimsPrincipal principal,
            PanelUserService panelUsers,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }

            await panelUsers.RecordActivityAsync(userId, ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }
}

public sealed record LoginRequest(string Email, string Password, bool RememberMe = false);
public sealed record LoginResponse(string Token, string Email);

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Orbita.Logging.Audit;

namespace Orbita.Api.Auth;

public static class JwtSecurityStampValidator
{
    public const string SecurityStampClaimType = "sst";

    public static async Task ValidateAsync(
        TokenValidatedContext context,
        UserManager<IdentityUser> users)
    {
        var principal = context.Principal;
        if (principal is null)
        {
            return;
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            await LogRejectionAsync(context, "missing_user_id", "JWT rejected: missing user id claim.");
            context.Fail("Missing user id claim.");
            return;
        }

        var stampClaim = principal.FindFirstValue(SecurityStampClaimType);
        if (string.IsNullOrWhiteSpace(stampClaim))
        {
            await LogRejectionAsync(context, "missing_security_stamp", "JWT rejected: missing security stamp claim.");
            context.Fail("Missing security stamp claim.");
            return;
        }

        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            await LogRejectionAsync(context, "user_not_found", $"JWT rejected: user no longer exists ({userId}).");
            context.Fail("User no longer exists.");
            return;
        }

        if (!string.Equals(user.SecurityStamp, stampClaim, StringComparison.Ordinal))
        {
            await LogRejectionAsync(
                context,
                "security_stamp_mismatch",
                $"JWT rejected: security stamp mismatch ({user.Email ?? userId}).");
            context.Fail("Security stamp mismatch.");
        }
    }

    private static async Task LogRejectionAsync(TokenValidatedContext context, string reason, string message)
    {
        await GlobalLogger.Instance.LogAsync(
            message,
            DeskLinkAuditLogLevel.Warning,
            errorKey: $"auth.jwt.{reason}",
            properties: new Dictionary<string, object?>
            {
                ["http.path"] = context.HttpContext.Request.Path.Value
            });
    }
}
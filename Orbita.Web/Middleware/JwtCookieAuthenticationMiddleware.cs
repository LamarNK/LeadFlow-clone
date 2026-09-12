using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Auth;
using Orbita.Web.Options;
using Orbita.Web.Services;

namespace Orbita.Web.Middleware;

public sealed class JwtCookieAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        OrbitaAuthService auth,
        OrbitaApiClient api,
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
        else if (context.User.Identity?.IsAuthenticated == true
                 && NeedsAccessRefresh(context, context.User))
        {
            await api.RefreshSessionAsync(context.RequestAborted);
        }

        await next(context);
    }

    private static bool NeedsAccessRefresh(HttpContext context, ClaimsPrincipal user)
    {
        var authorizeData = context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>();
        if (authorizeData is not { Count: > 0 })
        {
            return false;
        }

        foreach (var requirement in authorizeData)
        {
            if (!string.IsNullOrWhiteSpace(requirement.Roles))
            {
                var allowedRoles = requirement.Roles
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!allowedRoles.Any(user.IsInRole))
                {
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(requirement.Policy)
                && !HasPolicyAccess(user, requirement.Policy))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPolicyAccess(ClaimsPrincipal user, string policy) =>
        policy switch
        {
            PanelPermissions.Balances or PanelPermissions.Listings =>
                user.IsInRole(PanelRoles.Admin) || user.IsInRole(PanelRoles.Operator),
            "OfficeStaff" =>
                user.IsInRole(PanelRoles.Admin) || user.IsInRole(PanelRoles.OfficeLead),
            PanelRoles.Admin =>
                user.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Administration),
            PanelPermissions.CrmBoard or PanelPermissions.CrmTasks or PanelPermissions.CrmAnalytics =>
                user.HasClaim(PanelPermissions.ClaimType, policy)
                || user.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Crm),
            _ => user.HasClaim(PanelPermissions.ClaimType, policy)
        };
}

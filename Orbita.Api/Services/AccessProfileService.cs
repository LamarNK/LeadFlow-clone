using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Orbita.Contracts;
using Orbita.Logging.Audit;

namespace Orbita.Api.Services;

public sealed class AccessProfileService(
    RoleManager<IdentityRole> roles,
    UserManager<IdentityUser> users,
    PanelAuditService audit)
{
    public async Task EnsureDefaultsAsync(CancellationToken ct = default)
    {
        foreach (var profile in PanelPermissions.Profiles)
        {
            var role = await roles.FindByNameAsync(profile.Role);
            if (role is null)
            {
                continue;
            }

            var claims = await roles.GetClaimsAsync(role);
            if (!claims.Any(x => x.Type == PanelPermissions.ConfigurationClaimType))
            {
                foreach (var claim in claims.Where(x => x.Type == PanelPermissions.ClaimType))
                {
                    await roles.RemoveClaimAsync(role, claim);
                }

                foreach (var permission in PanelPermissions.DefaultForRole(profile.Role))
                {
                    await roles.AddClaimAsync(role, new Claim(PanelPermissions.ClaimType, permission));
                }

                await roles.AddClaimAsync(role, new Claim(PanelPermissions.ConfigurationClaimType, "true"));
                claims = await roles.GetClaimsAsync(role);
            }

            var upgraded = false;
            if (await UpgradeManagerDeskOnlyAsync(role, claims))
            {
                upgraded = true;
                claims = await roles.GetClaimsAsync(role);
            }

            if (await UpgradeCrmAnalyticsAccessAsync(role, claims))
            {
                upgraded = true;
                claims = await roles.GetClaimsAsync(role);
            }

            if (await UpgradeCrmTeamAccessAsync(role, claims))
            {
                upgraded = true;
            }

            if (upgraded)
            {
                foreach (var user in await users.GetUsersInRoleAsync(profile.Role))
                {
                    await users.UpdateSecurityStampAsync(user);
                }
            }
        }
    }

    /// <summary>
    /// Manager is desk-only: no analytics/team tabs by default. Strip legacy grants once.
    /// </summary>
    private async Task<bool> UpgradeManagerDeskOnlyAsync(IdentityRole role, IEnumerable<Claim> claims)
    {
        if (!string.Equals(role.Name, PanelRoles.Manager, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (claims.Any(claim => claim.Type == PanelPermissions.PermissionUpgradeClaimType
                                && claim.Value == PanelPermissions.ManagerDeskOnlyUpgrade))
        {
            return false;
        }

        var changed = false;
        foreach (var claim in claims.Where(c =>
                     c.Type == PanelPermissions.ClaimType
                     && (c.Value == PanelPermissions.CrmAnalytics || c.Value == PanelPermissions.CrmTeam)).ToList())
        {
            await roles.RemoveClaimAsync(role, claim);
            changed = true;
        }

        await roles.AddClaimAsync(
            role,
            new Claim(PanelPermissions.PermissionUpgradeClaimType, PanelPermissions.ManagerDeskOnlyUpgrade));
        return changed;
    }

    private async Task<bool> UpgradeCrmAnalyticsAccessAsync(IdentityRole role, IEnumerable<Claim> claims)
    {
        // Manager must stay desk-only — never re-grant analytics via legacy board→analytics upgrade.
        if (string.Equals(role.Name, PanelRoles.Manager, StringComparison.OrdinalIgnoreCase))
        {
            if (!claims.Any(claim => claim.Type == PanelPermissions.PermissionUpgradeClaimType
                                    && claim.Value == PanelPermissions.CrmAnalyticsUpgrade))
            {
                await roles.AddClaimAsync(
                    role,
                    new Claim(PanelPermissions.PermissionUpgradeClaimType, PanelPermissions.CrmAnalyticsUpgrade));
            }

            return false;
        }

        if (claims.Any(claim => claim.Type == PanelPermissions.PermissionUpgradeClaimType
                                && claim.Value == PanelPermissions.CrmAnalyticsUpgrade))
        {
            return false;
        }

        var permissions = claims
            .Where(claim => claim.Type == PanelPermissions.ClaimType)
            .Select(claim => claim.Value)
            .ToArray();
        var addAnalytics = PanelPermissions.NeedsCrmAnalyticsUpgrade(permissions);
        if (addAnalytics)
        {
            await roles.AddClaimAsync(role, new Claim(PanelPermissions.ClaimType, PanelPermissions.CrmAnalytics));
        }

        await roles.AddClaimAsync(
            role,
            new Claim(PanelPermissions.PermissionUpgradeClaimType, PanelPermissions.CrmAnalyticsUpgrade));
        return addAnalytics;
    }

    private async Task<bool> UpgradeCrmTeamAccessAsync(IdentityRole role, IEnumerable<Claim> claims)
    {
        if (claims.Any(claim => claim.Type == PanelPermissions.PermissionUpgradeClaimType
                                && claim.Value == PanelPermissions.CrmTeamUpgrade))
        {
            return false;
        }

        var permissions = claims
            .Where(claim => claim.Type == PanelPermissions.ClaimType)
            .Select(claim => claim.Value)
            .ToArray();
        var addTeam = PanelPermissions.NeedsCrmTeamUpgrade(permissions);
        if (addTeam)
        {
            await roles.AddClaimAsync(role, new Claim(PanelPermissions.ClaimType, PanelPermissions.CrmTeam));
        }

        await roles.AddClaimAsync(
            role,
            new Claim(PanelPermissions.PermissionUpgradeClaimType, PanelPermissions.CrmTeamUpgrade));
        return addTeam;
    }

    public async Task<IReadOnlyList<AccessProfileDto>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureDefaultsAsync(ct);

        var profiles = new List<AccessProfileDto>(PanelPermissions.Profiles.Count);
        foreach (var profile in PanelPermissions.Profiles)
        {
            var role = await roles.FindByNameAsync(profile.Role);
            var claims = role is null ? [] : await roles.GetClaimsAsync(role);
            var permissions = PanelPermissions.Normalize(
                claims.Where(x => x.Type == PanelPermissions.ClaimType).Select(x => x.Value));
            profiles.Add(new AccessProfileDto(profile.Id, profile.Name, profile.Description, permissions));
        }

        return profiles;
    }

    public async Task<IReadOnlyList<string>> GetPermissionsForRolesAsync(
        IEnumerable<string> roleNames,
        CancellationToken ct = default)
    {
        await EnsureDefaultsAsync(ct);

        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var roleName in roleNames)
        {
            var role = await roles.FindByNameAsync(roleName);
            if (role is null)
            {
                continue;
            }

            foreach (var permission in (await roles.GetClaimsAsync(role))
                         .Where(x => x.Type == PanelPermissions.ClaimType)
                         .Select(x => x.Value))
            {
                permissions.Add(permission);
            }
        }

        return PanelPermissions.Normalize(permissions);
    }

    public async Task<string?> UpdateAsync(
        string profileId,
        IReadOnlyList<string> requestedPermissions,
        AuditActor actor,
        CancellationToken ct = default)
    {
        var profile = PanelPermissions.Profiles.FirstOrDefault(x =>
            string.Equals(x.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return "Профиль доступа не найден.";
        }

        var permissions = PanelPermissions.Normalize(requestedPermissions);
        if (permissions.Count == 0)
        {
            return "У профиля должен остаться хотя бы один доступный раздел.";
        }

        if (profile.Role == PanelRoles.Admin && !permissions.Contains(PanelPermissions.Administration, StringComparer.Ordinal))
        {
            return "У администратора должно остаться право «Администрирование».";
        }

        var role = await roles.FindByNameAsync(profile.Role);
        if (role is null)
        {
            return "Роль профиля не найдена.";
        }

        var currentClaims = await roles.GetClaimsAsync(role);
        foreach (var claim in currentClaims.Where(x => x.Type == PanelPermissions.ClaimType))
        {
            var result = await roles.RemoveClaimAsync(role, claim);
            if (!result.Succeeded)
            {
                return string.Join("; ", result.Errors.Select(x => x.Description));
            }
        }

        foreach (var permission in permissions)
        {
            var result = await roles.AddClaimAsync(role, new Claim(PanelPermissions.ClaimType, permission));
            if (!result.Succeeded)
            {
                return string.Join("; ", result.Errors.Select(x => x.Description));
            }
        }

        if (!currentClaims.Any(x => x.Type == PanelPermissions.ConfigurationClaimType))
        {
            var result = await roles.AddClaimAsync(role, new Claim(PanelPermissions.ConfigurationClaimType, "true"));
            if (!result.Succeeded)
            {
                return string.Join("; ", result.Errors.Select(x => x.Description));
            }
        }

        foreach (var user in await users.GetUsersInRoleAsync(profile.Role))
        {
            await users.UpdateSecurityStampAsync(user);
        }

        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.AccessProfileUpdated,
            "access-profile",
            profile.Id,
            $"permissions={string.Join(',', permissions)}",
            actor.IpAddress,
            ct);

        await GlobalLogger.Instance.LogAsync(
            $"Panel access profile updated ({profile.Id}).",
            DeskLinkAuditLogLevel.Warning);

        return null;
    }
}

using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;
using Orbita.Logging.Audit;

namespace Orbita.Api.Services;

public sealed class PanelUserService(
    UserManager<IdentityUser> users,
    PanelAuditService audit,
    OrbitaDbContext db)
{
    public async Task<long> GetAccessVersionAsync(string userId, CancellationToken ct = default) =>
        await db.PanelUserProfiles.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.AccessVersion)
            .FirstOrDefaultAsync(ct);

    private async Task BumpAccessVersionAsync(string userId, CancellationToken ct)
    {
        var profile = await db.PanelUserProfiles.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (profile is null)
        {
            profile = new PanelUserProfileEntity { UserId = userId };
            db.PanelUserProfiles.Add(profile);
        }

        profile.AccessVersion++;
        await db.SaveChangesAsync(ct);
    }
    public async Task<IReadOnlyList<PanelUserDto>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<PanelUserDto>();
        foreach (var user in await users.Users.OrderBy(u => u.Email).ToListAsync(ct))
        {
            result.Add(await MapAsync(user, ct));
        }

        return result;
    }

    /// <summary>
    /// Users in <paramref name="officeId"/> with Manager or SeniorManager role (office-staff roster).
    /// </summary>
    public async Task<IReadOnlyList<PanelUserDto>> ListStaffByOfficeAsync(Guid officeId, CancellationToken ct = default)
    {
        var userIds = await db.PanelUserProfiles
            .AsNoTracking()
            .Where(x => x.OfficeId == officeId)
            .Select(x => x.UserId)
            .ToListAsync(ct);

        var result = new List<PanelUserDto>();
        foreach (var userId in userIds)
        {
            var user = await users.FindByIdAsync(userId);
            if (user is null)
            {
                continue;
            }

            var dto = await MapAsync(user, ct);
            if (OfficeStaffRules.IsAssignableRole(dto.Role))
            {
                result.Add(dto);
            }
        }

        return result
            .OrderBy(x => x.Role == PanelRoles.SeniorManager ? 0 : 1)
            .ThenBy(x => string.IsNullOrWhiteSpace(x.FullName) ? x.Email : x.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Users in <paramref name="officeId"/> who may use office telephony.
    /// Unlike the editable staff roster, this also includes the office lead so
    /// a phone extension and outbound line can be assigned to them.
    /// </summary>
    public async Task<IReadOnlyList<PanelUserDto>> ListTelephonyUsersByOfficeAsync(
        Guid officeId,
        CancellationToken ct = default)
    {
        var userIds = await db.PanelUserProfiles
            .AsNoTracking()
            .Where(x => x.OfficeId == officeId)
            .Select(x => x.UserId)
            .ToListAsync(ct);

        var result = new List<PanelUserDto>();
        foreach (var userId in userIds)
        {
            var user = await users.FindByIdAsync(userId);
            if (user is null)
            {
                continue;
            }

            var dto = await MapAsync(user, ct);
            if (PanelRoles.CrmDeskRoles.Contains(dto.Role, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(dto);
            }
        }

        return result
            .OrderBy(x => PanelRoles.Normalize(x.Role) switch
            {
                PanelRoles.OfficeLead => 0,
                PanelRoles.SeniorManager => 1,
                _ => 2
            })
            .ThenBy(x => string.IsNullOrWhiteSpace(x.FullName) ? x.Email : x.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<PanelUserDto?> GetByIdAsync(string userId, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId);
        return user is null ? null : await MapAsync(user, ct);
    }

    public async Task<PanelProfileDto?> GetProfileAsync(string userId, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return null;
        }

        var roles = await users.GetRolesAsync(user);
        var role = roles.FirstOrDefault(r => PanelRoles.All.Contains(r, StringComparer.OrdinalIgnoreCase))
                   ?? PanelRoles.Operator;
        var (officeId, officeName, fullName, _) = await GetProfileInfoAsync(userId, role, ct);
        return new PanelProfileDto(user.Email ?? user.UserName ?? string.Empty, role, officeId, officeName, fullName);
    }

    public async Task<(PanelUserDto? User, string? Error)> CreateAsync(
        string email,
        string password,
        string? role,
        Guid? officeId,
        string? fullName,
        AuditActor actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return (null, "Email и пароль обязательны.");
        }

        var normalizedFullName = NormalizeFullName(fullName);
        if (normalizedFullName is null)
        {
            return (null, "ФИО обязательно и не должно превышать 256 символов.");
        }

        var normalizedEmail = email.Trim();
        if (await users.FindByEmailAsync(normalizedEmail) is not null)
        {
            return (null, "Пользователь с таким email уже существует.");
        }

        var normalizedRole = PanelRoles.Normalize(role);
        var officeError = await ValidateOfficeAssignmentAsync(normalizedRole, officeId, ct);
        if (officeError is not null)
        {
            return (null, officeError);
        }

        var user = new IdentityUser
        {
            UserName = normalizedEmail,
            Email = normalizedEmail,
            EmailConfirmed = true
        };

        var createResult = await users.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            return (null, string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }

        var assignError = await ApplyRoleAsync(user, normalizedRole);
        if (assignError is not null)
        {
            await users.DeleteAsync(user);
            return (null, assignError);
        }

        db.PanelUserProfiles.Add(new PanelUserProfileEntity
        {
            UserId = user.Id,
            FullName = normalizedFullName,
            OfficeId = PanelRoles.IsGlobalAdmin(normalizedRole) ? null : officeId
        });
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.UserCreated,
            "user",
            user.Id,
            $"role={normalizedRole};office={officeId};fullName={normalizedFullName}",
            actor.IpAddress,
            ct);

        await GlobalLogger.Instance.LogAsync(
            $"Panel user created ({user.Email}, role={normalizedRole}).",
            DeskLinkAuditLogLevel.Info);

        return (await MapAsync(user, ct), null);
    }

    public async Task<(PanelUserDto? User, string? Error)> SetFullNameAsync(
        string id,
        string? fullName,
        AuditActor actor,
        CancellationToken ct = default)
    {
        var normalizedFullName = NormalizeFullName(fullName);
        if (normalizedFullName is null)
        {
            return (null, "ФИО обязательно и не должно превышать 256 символов.");
        }

        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return (null, "Пользователь не найден.");
        }

        var profile = await db.PanelUserProfiles.FirstOrDefaultAsync(x => x.UserId == id, ct);
        if (profile is null)
        {
            profile = new PanelUserProfileEntity { UserId = id, FullName = normalizedFullName };
            db.PanelUserProfiles.Add(profile);
        }
        else
        {
            profile.FullName = normalizedFullName;
        }

        await db.SaveChangesAsync(ct);
        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.UserFullNameUpdated,
            "user",
            id,
            normalizedFullName,
            actor.IpAddress,
            ct);

        return (await MapAsync(user, ct), null);
    }

    public async Task<(PanelUserDto? User, string? Error)> SetEmailAsync(
        string id,
        string? email,
        AuditActor actor,
        CancellationToken ct = default)
    {
        var normalizedEmail = email?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedEmail)
            || normalizedEmail.Length > 256
            || !new EmailAddressAttribute().IsValid(normalizedEmail))
        {
            return (null, "Укажите корректный email длиной не более 256 символов.");
        }

        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return (null, "Пользователь не найден.");
        }

        var existing = await users.FindByEmailAsync(normalizedEmail);
        if (existing is not null && !string.Equals(existing.Id, id, StringComparison.Ordinal))
        {
            return (null, "Пользователь с таким email уже существует.");
        }

        var previousEmail = user.Email ?? user.UserName ?? string.Empty;
        if (string.Equals(previousEmail, normalizedEmail, StringComparison.Ordinal))
        {
            return (await MapAsync(user, ct), null);
        }

        user.Email = normalizedEmail;
        user.UserName = normalizedEmail;
        user.EmailConfirmed = true;

        var updateResult = await users.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            return (null, string.Join("; ", updateResult.Errors.Select(x => x.Description)));
        }

        await BumpAccessVersionAsync(user.Id, ct);
        await users.UpdateSecurityStampAsync(user);

        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.UserEmailUpdated,
            "user",
            id,
            $"{previousEmail} -> {normalizedEmail}",
            actor.IpAddress,
            ct);

        await GlobalLogger.Instance.LogAsync(
            $"Panel user email updated ({previousEmail} -> {normalizedEmail}).",
            DeskLinkAuditLogLevel.Info);

        return (await MapAsync(user, ct), null);
    }

    public async Task<string?> DeleteAsync(
        string id,
        string? currentUserId,
        AuditActor actor,
        CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return "Пользователь не найден.";
        }

        if (string.Equals(user.Id, currentUserId, StringComparison.Ordinal))
        {
            return "Нельзя удалить текущего пользователя.";
        }

        if (await users.Users.CountAsync(ct) <= 1)
        {
            return "Нельзя удалить последнего пользователя.";
        }

        if (await IsAdminAsync(user) && await CountAdminsAsync(ct) <= 1)
        {
            return "Нельзя удалить последнего администратора.";
        }

        var email = user.Email ?? user.UserName ?? id;
        var profile = await db.PanelUserProfiles.FirstOrDefaultAsync(x => x.UserId == id, ct);
        if (profile is not null)
        {
            db.PanelUserProfiles.Remove(profile);
            await db.SaveChangesAsync(ct);
        }

        await PanelUserPresenceStore.DeleteUserAsync(db, id, ct);

        var deleteResult = await users.DeleteAsync(user);
        if (!deleteResult.Succeeded)
        {
            return string.Join("; ", deleteResult.Errors.Select(e => e.Description));
        }

        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.UserDeleted,
            "user",
            id,
            email,
            actor.IpAddress,
            ct);

        await GlobalLogger.Instance.LogAsync(
            $"Panel user deleted ({email}).",
            DeskLinkAuditLogLevel.Warning);

        return null;
    }

    public async Task<string?> ResetPasswordAsync(
        string id,
        string password,
        AuditActor actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return "Пароль обязателен.";
        }

        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return "Пользователь не найден.";
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var resetResult = await users.ResetPasswordAsync(user, token, password);
        if (!resetResult.Succeeded)
        {
            return string.Join("; ", resetResult.Errors.Select(e => e.Description));
        }

        await users.UpdateSecurityStampAsync(user);

        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.UserPasswordReset,
            "user",
            id,
            user.Email,
            actor.IpAddress,
            ct);

        await GlobalLogger.Instance.LogAsync(
            $"Panel user password reset ({user.Email}).",
            DeskLinkAuditLogLevel.Warning);

        return null;
    }

    public async Task<string?> ChangeOwnPasswordAsync(
        string userId,
        string currentPassword,
        string newPassword,
        AuditActor actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword))
        {
            return "Текущий и новый пароль обязательны.";
        }

        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return "Пользователь не найден.";
        }

        if (!await users.CheckPasswordAsync(user, currentPassword))
        {
            await GlobalLogger.Instance.LogAsync(
                $"Panel password change failed: invalid current password ({user.Email}).",
                DeskLinkAuditLogLevel.Warning,
                errorKey: "auth.password.invalid_current");
            return "Неверный текущий пароль.";
        }

        var changeResult = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!changeResult.Succeeded)
        {
            return string.Join("; ", changeResult.Errors.Select(e => e.Description));
        }

        await users.UpdateSecurityStampAsync(user);

        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.UserPasswordChanged,
            "user",
            userId,
            null,
            actor.IpAddress,
            ct);

        await GlobalLogger.Instance.LogAsync(
            $"Panel user changed own password ({user.Email}).",
            DeskLinkAuditLogLevel.Info);

        return null;
    }

    public async Task<(PanelUserDto? User, string? Error)> SetRoleAsync(
        string id,
        string? role,
        string? currentUserId,
        AuditActor actor,
        CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return (null, "Пользователь не найден.");
        }

        var normalizedRole = PanelRoles.Normalize(role);
        if (await IsAdminAsync(user)
            && normalizedRole != PanelRoles.Admin
            && string.Equals(user.Id, currentUserId, StringComparison.Ordinal))
        {
            return (null, "Нельзя снять роль администратора у текущего пользователя.");
        }

        if (await IsAdminAsync(user)
            && normalizedRole != PanelRoles.Admin
            && await CountAdminsAsync(ct) <= 1)
        {
            return (null, "Нельзя снять роль у последнего администратора.");
        }

        var assignError = await ApplyRoleAsync(user, normalizedRole);
        if (assignError is not null)
        {
            return (null, assignError);
        }

        var profile = await db.PanelUserProfiles.FirstOrDefaultAsync(x => x.UserId == id, ct);
        if (PanelRoles.IsGlobalAdmin(normalizedRole))
        {
            if (profile is null)
            {
                db.PanelUserProfiles.Add(new PanelUserProfileEntity { UserId = id, OfficeId = null });
            }
            else
            {
                profile.OfficeId = null;
            }
        }
        else if (profile?.OfficeId is null)
        {
            var defaultOffice = await db.Offices.OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
            if (defaultOffice is null)
            {
                return (null, "Сначала создайте офис и назначьте его пользователю.");
            }

            if (profile is null)
            {
                db.PanelUserProfiles.Add(new PanelUserProfileEntity { UserId = id, OfficeId = defaultOffice.Id });
            }
            else
            {
                profile.OfficeId = defaultOffice.Id;
            }
        }

        await db.SaveChangesAsync(ct);
        await BumpAccessVersionAsync(user.Id, ct);
        await users.UpdateSecurityStampAsync(user);

        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.UserRoleUpdated,
            "user",
            id,
            $"role={normalizedRole}",
            actor.IpAddress,
            ct);

        await GlobalLogger.Instance.LogAsync(
            $"Panel user role updated ({user.Email}, role={normalizedRole}).",
            DeskLinkAuditLogLevel.Info);

        return (await MapAsync(user, ct), null);
    }

    public async Task<(PanelUserDto? User, string? Error)> SetPermissionOverrideAsync(
        string id,
        bool useProfilePermissions,
        IReadOnlyList<string> requestedPermissions,
        AuditActor actor,
        CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return (null, "Пользователь не найден.");
        }

        var permissions = PanelPermissions.Normalize(requestedPermissions);
        if (!useProfilePermissions && permissions.Count == 0)
        {
            return (null, "Выберите хотя бы один доступный раздел или используйте права профиля.");
        }

        if (!useProfilePermissions
            && await IsAdminAsync(user)
            && !permissions.Contains(PanelPermissions.Administration, StringComparer.Ordinal))
        {
            return (null, "У администратора должно остаться право «Администрирование».");
        }

        var currentClaims = await users.GetClaimsAsync(user);
        foreach (var claim in currentClaims.Where(claim =>
                     claim.Type is PanelPermissions.ClaimType or PanelPermissions.UserPermissionOverrideClaimType))
        {
            var result = await users.RemoveClaimAsync(user, claim);
            if (!result.Succeeded)
            {
                return (null, string.Join("; ", result.Errors.Select(error => error.Description)));
            }
        }

        if (!useProfilePermissions)
        {
            foreach (var permission in permissions)
            {
                var result = await users.AddClaimAsync(user, new Claim(PanelPermissions.ClaimType, permission));
                if (!result.Succeeded)
                {
                    return (null, string.Join("; ", result.Errors.Select(error => error.Description)));
                }
            }

            var markerResult = await users.AddClaimAsync(
                user,
                new Claim(PanelPermissions.UserPermissionOverrideClaimType, "true"));
            if (!markerResult.Succeeded)
            {
                return (null, string.Join("; ", markerResult.Errors.Select(error => error.Description)));
            }

            if (!currentClaims.Any(claim => claim.Type == PanelPermissions.PermissionUpgradeClaimType
                                            && claim.Value == PanelPermissions.CrmAnalyticsUpgrade))
            {
                var upgradeMarkerResult = await users.AddClaimAsync(
                    user,
                    new Claim(PanelPermissions.PermissionUpgradeClaimType, PanelPermissions.CrmAnalyticsUpgrade));
                if (!upgradeMarkerResult.Succeeded)
                {
                    return (null, string.Join("; ", upgradeMarkerResult.Errors.Select(error => error.Description)));
                }
            }

            if (!currentClaims.Any(claim => claim.Type == PanelPermissions.PermissionUpgradeClaimType
                                            && claim.Value == PanelPermissions.CrmTeamUpgrade))
            {
                var teamUpgradeMarkerResult = await users.AddClaimAsync(
                    user,
                    new Claim(PanelPermissions.PermissionUpgradeClaimType, PanelPermissions.CrmTeamUpgrade));
                if (!teamUpgradeMarkerResult.Succeeded)
                {
                    return (null, string.Join("; ", teamUpgradeMarkerResult.Errors.Select(error => error.Description)));
                }
            }
        }

        await BumpAccessVersionAsync(user.Id, ct);
        await users.UpdateSecurityStampAsync(user);
        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.UserPermissionsUpdated,
            "user",
            id,
            useProfilePermissions ? "profile" : $"permissions={string.Join(',', permissions)}",
            actor.IpAddress,
            ct);

        await GlobalLogger.Instance.LogAsync(
            $"Panel user permissions updated ({user.Email}, source={(useProfilePermissions ? "profile" : "override")}).",
            DeskLinkAuditLogLevel.Warning);

        return (await MapAsync(user, ct), null);
    }

    public async Task<(PanelUserDto? User, string? Error)> SetCardDeletionPermissionAsync(
        string id, bool enabled, AuditActor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor.UserId)
            || !await CrmCardDeletionAccess.IsAdministratorAsync(db, actor.UserId, ct))
            return (null, "Изменять право удаления карточек может только администратор.");

        var user = await users.FindByIdAsync(id);
        if (user is null) return (null, "Пользователь не найден.");

        var grants = await db.UserClaims.Where(claim => claim.UserId == id
            && claim.ClaimType == CrmCardDeletionPermission.ClaimType).ToListAsync(ct);
        if ((!enabled && grants.Count == 0)
            || (enabled && grants.Count == 1 && grants[0].ClaimValue == CrmCardDeletionPermission.GrantedValue))
            return (await MapAsync(user, ct), null);

        db.UserClaims.RemoveRange(grants);
        if (enabled)
            db.UserClaims.Add(new IdentityUserClaim<string>
            {
                UserId = id,
                ClaimType = CrmCardDeletionPermission.ClaimType,
                ClaimValue = CrmCardDeletionPermission.GrantedValue
            });

        // Audit service saves grant changes and their audit entry together.
        // No relogin is needed: every card GET/DELETE reads this grant from the DB.
        await audit.LogAsync(actor.UserId, actor.Email,
            PanelAuditActions.UserCardDeletionPermissionUpdated, "user", id,
            enabled ? "enabled" : "disabled", actor.IpAddress, ct);
        return (await MapAsync(user, ct), null);
    }

    public async Task<IReadOnlyList<string>?> GetPermissionOverrideAsync(IdentityUser user)
    {
        var claims = await users.GetClaimsAsync(user);
        if (!claims.Any(claim => claim.Type == PanelPermissions.UserPermissionOverrideClaimType))
        {
            return null;
        }

        if (!claims.Any(claim => claim.Type == PanelPermissions.PermissionUpgradeClaimType
                                 && claim.Value == PanelPermissions.CrmAnalyticsUpgrade))
        {
            var permissions = claims
                .Where(claim => claim.Type == PanelPermissions.ClaimType)
                .Select(claim => claim.Value)
                .ToArray();
            if (PanelPermissions.NeedsCrmAnalyticsUpgrade(permissions))
            {
                await users.AddClaimAsync(user, new Claim(PanelPermissions.ClaimType, PanelPermissions.CrmAnalytics));
            }

            await users.AddClaimAsync(
                user,
                new Claim(PanelPermissions.PermissionUpgradeClaimType, PanelPermissions.CrmAnalyticsUpgrade));
            claims = await users.GetClaimsAsync(user);
        }

        if (!claims.Any(claim => claim.Type == PanelPermissions.PermissionUpgradeClaimType
                                 && claim.Value == PanelPermissions.CrmTeamUpgrade))
        {
            var permissions = claims
                .Where(claim => claim.Type == PanelPermissions.ClaimType)
                .Select(claim => claim.Value)
                .ToArray();
            if (PanelPermissions.NeedsCrmTeamUpgrade(permissions))
            {
                await users.AddClaimAsync(user, new Claim(PanelPermissions.ClaimType, PanelPermissions.CrmTeam));
            }

            await users.AddClaimAsync(
                user,
                new Claim(PanelPermissions.PermissionUpgradeClaimType, PanelPermissions.CrmTeamUpgrade));
            claims = await users.GetClaimsAsync(user);
        }

        if (!claims.Any(claim => claim.Type == PanelPermissions.PermissionUpgradeClaimType
                                 && claim.Value == PanelPermissions.ListingsUpgrade))
        {
            var permissions = claims
                .Where(claim => claim.Type == PanelPermissions.ClaimType)
                .Select(claim => claim.Value)
                .ToArray();
            var roles = await users.GetRolesAsync(user);
            if (PanelPermissions.NeedsListingsUpgrade(roles.FirstOrDefault(), permissions))
            {
                await users.AddClaimAsync(user, new Claim(PanelPermissions.ClaimType, PanelPermissions.Listings));
            }

            await users.AddClaimAsync(
                user,
                new Claim(PanelPermissions.PermissionUpgradeClaimType, PanelPermissions.ListingsUpgrade));
            claims = await users.GetClaimsAsync(user);
        }

        return PanelPermissions.Normalize(
            claims.Where(claim => claim.Type == PanelPermissions.ClaimType).Select(claim => claim.Value));
    }

    public async Task<(PanelUserDto? User, string? Error)> SetOfficeAsync(
        string id,
        Guid? officeId,
        AuditActor actor,
        CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return (null, "Пользователь не найден.");
        }

        if (await IsAdminAsync(user))
        {
            return (null, "Администратор не привязан к офису.");
        }

        var officeError = await ValidateOfficeAssignmentAsync(PanelRoles.Operator, officeId, ct);
        if (officeError is not null)
        {
            return (null, officeError);
        }

        var profile = await db.PanelUserProfiles.FirstOrDefaultAsync(x => x.UserId == id, ct);
        if (profile is null)
        {
            profile = new PanelUserProfileEntity { UserId = id, OfficeId = officeId };
            db.PanelUserProfiles.Add(profile);
        }
        else
        {
            profile.OfficeId = officeId;
        }

        await db.SaveChangesAsync(ct);
        await BumpAccessVersionAsync(user.Id, ct);
        await users.UpdateSecurityStampAsync(user);

        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditOfficeActions.UserOfficeUpdated,
            "user",
            id,
            $"office={officeId}",
            actor.IpAddress,
            ct);

        return (await MapAsync(user, ct), null);
    }

    public async Task<(PanelUserDto? User, string? Error)> LockAsync(
        string id,
        string? currentUserId,
        AuditActor actor,
        CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return (null, "Пользователь не найден.");
        }

        if (string.Equals(user.Id, currentUserId, StringComparison.Ordinal))
        {
            return (null, "Нельзя заблокировать текущего пользователя.");
        }

        if (await IsAdminAsync(user) && await CountAdminsAsync(ct) <= 1)
        {
            return (null, "Нельзя заблокировать последнего администратора.");
        }

        if (IsLocked(user))
        {
            return (await MapAsync(user, ct), null);
        }

        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(100));

        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.UserLocked,
            "user",
            id,
            user.Email,
            actor.IpAddress,
            ct);

        await GlobalLogger.Instance.LogAsync(
            $"Panel user locked ({user.Email}).",
            DeskLinkAuditLogLevel.Warning);

        return (await MapAsync(user, ct), null);
    }

    public async Task<(PanelUserDto? User, string? Error)> UnlockAsync(
        string id,
        AuditActor actor,
        CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return (null, "Пользователь не найден.");
        }

        if (!IsLocked(user))
        {
            return (await MapAsync(user, ct), null);
        }

        await users.SetLockoutEndDateAsync(user, null);

        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.UserUnlocked,
            "user",
            id,
            user.Email,
            actor.IpAddress,
            ct);

        await GlobalLogger.Instance.LogAsync(
            $"Panel user unlocked ({user.Email}).",
            DeskLinkAuditLogLevel.Info);

        return (await MapAsync(user, ct), null);
    }

    public async Task<string?> RevokeSessionsAsync(
        string id,
        string? currentUserId,
        AuditActor actor,
        CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return "Пользователь не найден.";
        }

        if (string.Equals(user.Id, currentUserId, StringComparison.Ordinal))
        {
            return "Нельзя завершить сессии текущего пользователя.";
        }

        await users.UpdateSecurityStampAsync(user);

        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.UserSessionsRevoked,
            "user",
            id,
            user.Email,
            actor.IpAddress,
            ct);

        await GlobalLogger.Instance.LogAsync(
            $"Panel user sessions revoked ({user.Email}).",
            DeskLinkAuditLogLevel.Warning);

        return null;
    }

    public async Task<Guid?> GetOfficeIdForUserAsync(string userId, CancellationToken ct = default) =>
        await db.PanelUserProfiles
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.OfficeId)
            .FirstOrDefaultAsync(ct);

    /// <summary>Records an authenticated browser heartbeat for presence reporting.</summary>
    public async Task RecordActivityAsync(string userId, CancellationToken ct = default)
    {
        var profile = await db.PanelUserProfiles
            .FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (profile is null)
        {
            return;
        }

        await PanelUserPresenceStore.TouchAsync(db, profile, DateTime.UtcNow, ct);
    }

    public Task<PanelUserPresenceHourSeriesDto> GetPresenceHourSeriesAsync(CancellationToken ct = default) =>
        PanelUserPresenceStore.GetHourSeriesAsync(db, DateTime.UtcNow, ct);

    private async Task<string?> ValidateOfficeAssignmentAsync(string role, Guid? officeId, CancellationToken ct)
    {
        if (!PanelRoles.RequiresOfficeAssignment(role))
        {
            return officeId is not null ? "Администратор не привязан к офису." : null;
        }

        if (officeId is not Guid resolvedOfficeId)
        {
            return "Для этого профиля нужно выбрать офис.";
        }

        if (!await db.Offices.AnyAsync(x => x.Id == resolvedOfficeId && x.IsEnabled, ct))
        {
            return "Офис не найден или отключён.";
        }

        return null;
    }

    private async Task<string?> ApplyRoleAsync(IdentityUser user, string role)
    {
        foreach (var existingRole in PanelRoles.All)
        {
            if (await users.IsInRoleAsync(user, existingRole))
            {
                var removeResult = await users.RemoveFromRoleAsync(user, existingRole);
                if (!removeResult.Succeeded)
                {
                    return string.Join("; ", removeResult.Errors.Select(e => e.Description));
                }
            }
        }

        var addResult = await users.AddToRoleAsync(user, role);
        if (!addResult.Succeeded)
        {
            return string.Join("; ", addResult.Errors.Select(e => e.Description));
        }

        return null;
    }

    private async Task<PanelUserDto> MapAsync(IdentityUser user, CancellationToken ct)
    {
        var roles = await users.GetRolesAsync(user);
        var role = roles.FirstOrDefault(r => PanelRoles.All.Contains(r, StringComparer.OrdinalIgnoreCase))
                   ?? PanelRoles.Operator;
        var (officeId, officeName, fullName, lastSeenAtUtc) = await GetProfileInfoAsync(user.Id, role, ct);
        var permissionOverride = await GetPermissionOverrideAsync(user);
        return new PanelUserDto(
            user.Id,
            user.Email ?? user.UserName ?? string.Empty,
            user.EmailConfirmed,
            role,
            IsLocked(user),
            officeId,
            officeName,
            fullName,
            permissionOverride,
            lastSeenAtUtc,
            PanelUserPresenceRules.IsOnline(lastSeenAtUtc, DateTime.UtcNow),
            CanDeleteCrmCards: await CrmCardDeletionAccess.HasExplicitGrantAsync(db, user.Id, ct));
    }

    private async Task<(Guid? OfficeId, string? OfficeName, string? FullName, DateTime? LastSeenAtUtc)> GetProfileInfoAsync(
        string userId,
        string role,
        CancellationToken ct)
    {
        var profile = await db.PanelUserProfiles
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => new ValueTuple<Guid?, string?, string?, DateTime?>(
                x.OfficeId,
                x.Office != null ? x.Office.Name : null,
                x.FullName,
                x.LastSeenAtUtc))
            .FirstOrDefaultAsync(ct);
        return PanelRoles.IsGlobalAdmin(role)
            ? (null, null, profile.Item3, profile.Item4)
            : profile;
    }

    private static string? NormalizeFullName(string? fullName)
    {
        var normalized = fullName?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > 256 ? null : normalized;
    }

    private static bool IsLocked(IdentityUser user) =>
        user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTimeOffset.UtcNow;

    private async Task<bool> IsAdminAsync(IdentityUser user) =>
        await users.IsInRoleAsync(user, PanelRoles.Admin);

    private async Task<int> CountAdminsAsync(CancellationToken ct)
    {
        _ = ct;
        return (await users.GetUsersInRoleAsync(PanelRoles.Admin)).Count;
    }
}

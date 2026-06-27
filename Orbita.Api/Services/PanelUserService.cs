using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orbita.Contracts;
using Orbita.Logging.Audit;

namespace Orbita.Api.Services;

public sealed class PanelUserService(UserManager<IdentityUser> users, PanelAuditService audit)
{
    public async Task<IReadOnlyList<PanelUserDto>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<PanelUserDto>();
        foreach (var user in await users.Users.OrderBy(u => u.Email).ToListAsync(ct))
        {
            result.Add(await MapAsync(user, ct));
        }

        return result;
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
        return new PanelProfileDto(user.Email ?? user.UserName ?? string.Empty, role);
    }

    public async Task<(PanelUserDto? User, string? Error)> CreateAsync(
        string email,
        string password,
        string? role,
        AuditActor actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return (null, "Email и пароль обязательны.");
        }

        var normalizedEmail = email.Trim();
        if (await users.FindByEmailAsync(normalizedEmail) is not null)
        {
            return (null, "Пользователь с таким email уже существует.");
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

        var normalizedRole = PanelRoles.Normalize(role);
        var assignError = await ApplyRoleAsync(user, normalizedRole);
        if (assignError is not null)
        {
            await users.DeleteAsync(user);
            return (null, assignError);
        }

        await audit.LogAsync(
            actor.UserId,
            actor.Email,
            PanelAuditActions.UserCreated,
            "user",
            user.Id,
            $"role={normalizedRole}",
            actor.IpAddress,
            ct);

        await GlobalLogger.Instance.LogAsync(
            $"Panel user created ({user.Email}, role={normalizedRole}).",
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
            && normalizedRole == PanelRoles.Operator
            && string.Equals(user.Id, currentUserId, StringComparison.Ordinal))
        {
            return (null, "Нельзя снять роль администратора у текущего пользователя.");
        }

        if (await IsAdminAsync(user)
            && normalizedRole == PanelRoles.Operator
            && await CountAdminsAsync(ct) <= 1)
        {
            return (null, "Нельзя снять роль у последнего администратора.");
        }

        var assignError = await ApplyRoleAsync(user, normalizedRole);
        if (assignError is not null)
        {
            return (null, assignError);
        }

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
        _ = ct;
        var roles = await users.GetRolesAsync(user);
        var role = roles.FirstOrDefault(r => PanelRoles.All.Contains(r, StringComparer.OrdinalIgnoreCase))
                   ?? PanelRoles.Operator;
        return new PanelUserDto(
            user.Id,
            user.Email ?? user.UserName ?? string.Empty,
            user.EmailConfirmed,
            role,
            IsLocked(user));
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
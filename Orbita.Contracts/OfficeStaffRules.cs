using System.Security.Claims;

namespace Orbita.Contracts;

/// <summary>
/// Office-scoped staff management for office leads (and global admins).
/// Assignable desk roles only: Manager and SeniorManager.
/// </summary>
public static class OfficeStaffRules
{
    public static readonly IReadOnlyList<string> AssignableRoles =
        [PanelRoles.Manager, PanelRoles.SeniorManager];

    public static bool CanManageStaff(string? actorRole)
    {
        var role = PanelRoles.Normalize(actorRole);
        return role is PanelRoles.Admin or PanelRoles.OfficeLead;
    }

    public static bool CanManageStaff(ClaimsPrincipal principal) =>
        principal.IsInRole(PanelRoles.Admin) || principal.IsInRole(PanelRoles.OfficeLead);

    public static bool IsAssignableRole(string? role)
    {
        var normalized = PanelRoles.Normalize(role);
        return normalized is PanelRoles.Manager or PanelRoles.SeniorManager;
    }

    /// <summary>Role dropdown for office-staff create/edit UI.</summary>
    public static IReadOnlyList<(string Value, string Label)> RoleOptions { get; } =
    [
        (PanelRoles.Manager, PanelRoles.Label(PanelRoles.Manager)),
        (PanelRoles.SeniorManager, PanelRoles.Label(PanelRoles.SeniorManager))
    ];

    public static string? ValidateAssignableRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return "Укажите должность.";
        }

        return IsAssignableRole(role)
            ? null
            : "Можно назначить только менеджера или старшего менеджера.";
    }

    /// <summary>
    /// Validates that an existing user may be managed as office staff in <paramref name="officeId"/>.
    /// </summary>
    public static string? ValidateManagedTarget(
        string? actorUserId,
        string targetUserId,
        string targetRole,
        Guid? targetOfficeId,
        Guid officeId,
        bool allowSelf = false)
    {
        if (!allowSelf
            && !string.IsNullOrWhiteSpace(actorUserId)
            && string.Equals(actorUserId, targetUserId, StringComparison.Ordinal))
        {
            return "Нельзя изменять собственный аккаунт здесь.";
        }

        if (!IsAssignableRole(targetRole))
        {
            return "Этого пользователя нельзя изменять из управления сотрудниками.";
        }

        if (targetOfficeId != officeId)
        {
            return "Пользователь принадлежит другому офису.";
        }

        return null;
    }
}

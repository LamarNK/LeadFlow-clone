using System.Security.Claims;

namespace Orbita.Contracts;

public static class PanelRoles
{
    public const string Admin = "Admin";
    /// <summary>
    /// Office-scoped elevated role: admin-like panel access within one office,
    /// without global administration.
    /// </summary>
    public const string OfficeLead = "OfficeLead";
    public const string Operator = "Operator";
    public const string Manager = "Manager";

    public static readonly IReadOnlyList<string> All = [Admin, OfficeLead, Operator, Manager];

    public static string Normalize(string? role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ? Admin :
        string.Equals(role, OfficeLead, StringComparison.OrdinalIgnoreCase) ? OfficeLead :
        string.Equals(role, Manager, StringComparison.OrdinalIgnoreCase) ? Manager : Operator;

    public static string ProfileIdForRole(string role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ? "admin" :
        string.Equals(role, OfficeLead, StringComparison.OrdinalIgnoreCase) ? "office-lead" :
        string.Equals(role, Manager, StringComparison.OrdinalIgnoreCase) ? "manager" : "operator";

    public static string RoleForProfileId(string? profileId) =>
        string.Equals(profileId, "admin", StringComparison.OrdinalIgnoreCase) ? Admin :
        string.Equals(profileId, "office-lead", StringComparison.OrdinalIgnoreCase) ? OfficeLead :
        string.Equals(profileId, "manager", StringComparison.OrdinalIgnoreCase) ? Manager : Operator;

    public static string Label(string? role) => Normalize(role) switch
    {
        Admin => "Администратор",
        OfficeLead => "Руководитель офиса",
        Manager => "Менеджер",
        _ => "Оператор"
    };

    /// <summary>Global admin: all offices, administration area.</summary>
    public static bool IsGlobalAdmin(string? role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase);

    public static bool IsGlobalAdmin(ClaimsPrincipal principal) =>
        principal.IsInRole(Admin);

    /// <summary>
    /// Elevated access within the current office (team CRM, all cards/managers).
    /// Global admins are included; office leads are elevated only inside their office scope.
    /// </summary>
    public static bool HasElevatedOfficeAccess(string? role)
    {
        var normalized = Normalize(role);
        return normalized is Admin or OfficeLead;
    }

    public static bool HasElevatedOfficeAccess(ClaimsPrincipal principal) =>
        principal.IsInRole(Admin) || principal.IsInRole(OfficeLead);

    public static bool RequiresOfficeAssignment(string? role) =>
        !IsGlobalAdmin(role);
}

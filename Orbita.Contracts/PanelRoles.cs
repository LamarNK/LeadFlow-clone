using System.Security.Claims;

namespace Orbita.Contracts;

public static class PanelRoles
{
    public const string Admin = "Admin";
    /// <summary>
    /// Office-scoped elevated role: full CRM + panel access within one office,
    /// without global administration.
    /// </summary>
    public const string OfficeLead = "OfficeLead";
    /// <summary>
    /// CRM senior: sees all office managers' leads, team board, reassignment.
    /// No system administration.
    /// </summary>
    public const string SeniorManager = "SeniorManager";
    public const string Operator = "Operator";
    public const string Manager = "Manager";

    public static readonly IReadOnlyList<string> All =
        [Admin, OfficeLead, SeniorManager, Operator, Manager];

    /// <summary>
    /// Roles that work the CRM desk: own cards, shifts, capacity, lead distribution.
    /// </summary>
    public static readonly IReadOnlyList<string> CrmDeskRoles =
        [Manager, SeniorManager, OfficeLead];

    /// <summary>
    /// Roles that receive cards from automatic CRM distribution.
    /// Office leads keep elevated CRM access and may run a shift, but never
    /// participate in the automatic lead/NDZ allocation.
    /// </summary>
    public static readonly IReadOnlyList<string> CrmDistributionRoles =
        [Manager, SeniorManager];

    public static string Normalize(string? role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ? Admin :
        string.Equals(role, OfficeLead, StringComparison.OrdinalIgnoreCase) ? OfficeLead :
        string.Equals(role, SeniorManager, StringComparison.OrdinalIgnoreCase) ? SeniorManager :
        string.Equals(role, Manager, StringComparison.OrdinalIgnoreCase) ? Manager : Operator;

    public static string ProfileIdForRole(string role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ? "admin" :
        string.Equals(role, OfficeLead, StringComparison.OrdinalIgnoreCase) ? "office-lead" :
        string.Equals(role, SeniorManager, StringComparison.OrdinalIgnoreCase) ? "senior-manager" :
        string.Equals(role, Manager, StringComparison.OrdinalIgnoreCase) ? "manager" : "operator";

    public static string RoleForProfileId(string? profileId) =>
        string.Equals(profileId, "admin", StringComparison.OrdinalIgnoreCase) ? Admin :
        string.Equals(profileId, "office-lead", StringComparison.OrdinalIgnoreCase) ? OfficeLead :
        string.Equals(profileId, "senior-manager", StringComparison.OrdinalIgnoreCase) ? SeniorManager :
        string.Equals(profileId, "manager", StringComparison.OrdinalIgnoreCase) ? Manager : Operator;

    public static string Label(string? role) => Normalize(role) switch
    {
        Admin => "Администратор",
        OfficeLead => "Руководитель",
        SeniorManager => "Старший менеджер",
        Manager => "Менеджер",
        _ => "Оператор"
    };

    /// <summary>Global admin: all offices, administration area.</summary>
    public static bool IsGlobalAdmin(string? role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase);

    public static bool IsGlobalAdmin(ClaimsPrincipal principal) =>
        principal.IsInRole(Admin);

    /// <summary>The office recording archive is reserved for administrators and office leads.</summary>
    public static bool CanAccessCallRecordings(ClaimsPrincipal principal) =>
        principal.IsInRole(Admin) || principal.IsInRole(OfficeLead);

    /// <summary>
    /// Elevated access within the current office (team CRM, all cards/managers).
    /// Global admins, office leads and senior managers.
    /// </summary>
    public static bool HasElevatedOfficeAccess(string? role)
    {
        var normalized = Normalize(role);
        return normalized is Admin or OfficeLead or SeniorManager;
    }

    public static bool HasElevatedOfficeAccess(ClaimsPrincipal principal) =>
        principal.IsInRole(Admin)
        || principal.IsInRole(OfficeLead)
        || principal.IsInRole(SeniorManager);

    /// <summary>
    /// Editing an already saved successful-close report is restricted to the
    /// office lead and the global administrator. Senior managers remain read-only.
    /// </summary>
    public static bool CanEditSuccessReport(ClaimsPrincipal principal) =>
        principal.IsInRole(Admin) || principal.IsInRole(OfficeLead);

    /// <summary>
    /// A successful-close report archive contains the complete candidate file set,
    /// so it is available only to the administrator and elevated office management.
    /// </summary>
    public static bool CanDownloadSuccessReportArchive(ClaimsPrincipal principal) =>
        principal.IsInRole(Admin)
        || principal.IsInRole(OfficeLead)
        || principal.IsInRole(SeniorManager);

    /// <summary>CRM desk role that can run a shift and work with cards.</summary>
    public static bool IsCrmDeskRole(string? role)
    {
        var normalized = Normalize(role);
        return normalized is Manager or SeniorManager or OfficeLead;
    }

    public static bool IsCrmDeskRole(ClaimsPrincipal principal) =>
        principal.IsInRole(Manager)
        || principal.IsInRole(SeniorManager)
        || principal.IsInRole(OfficeLead);

    public static bool RequiresOfficeAssignment(string? role) =>
        !IsGlobalAdmin(role);
}

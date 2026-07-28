namespace Orbita.Contracts;

public static class PanelRoles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Manager = "Manager";

    public static readonly IReadOnlyList<string> All = [Admin, Operator, Manager];

    public static string Normalize(string? role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ? Admin :
        string.Equals(role, Manager, StringComparison.OrdinalIgnoreCase) ? Manager : Operator;

    public static string ProfileIdForRole(string role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ? "admin" :
        string.Equals(role, Manager, StringComparison.OrdinalIgnoreCase) ? "manager" : "operator";

    public static string RoleForProfileId(string? profileId) =>
        string.Equals(profileId, "admin", StringComparison.OrdinalIgnoreCase) ? Admin :
        string.Equals(profileId, "manager", StringComparison.OrdinalIgnoreCase) ? Manager : Operator;
}
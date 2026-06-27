namespace Orbita.Contracts;

public static class PanelRoles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";

    public static readonly IReadOnlyList<string> All = [Admin, Operator];

    public static string Normalize(string? role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ? Admin : Operator;

    public static string ProfileIdForRole(string role) =>
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ? "admin" : "operator";

    public static string RoleForProfileId(string? profileId) =>
        string.Equals(profileId, "admin", StringComparison.OrdinalIgnoreCase) ? Admin : Operator;
}
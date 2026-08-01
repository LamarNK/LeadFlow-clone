using System.Security.Claims;
using Orbita.Contracts;

namespace Orbita.Tests;

internal static class TestPrincipalFactory
{
    /// <summary>Operator is office-bound via OfficeId claim.</summary>
    public static ClaimsPrincipal Operator(string userId, string name, Guid? officeId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Email, $"{userId}@test.local"),
            new(ClaimTypes.Role, PanelRoles.Operator)
        };
        if (officeId is Guid id)
        {
            claims.Add(new Claim(OfficeClaims.OfficeId, id.ToString("D")));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    public static ClaimsPrincipal Admin(string userId, string name)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Email, $"{userId}@test.local"),
            new(ClaimTypes.Role, PanelRoles.Admin)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    public static ClaimsPrincipal Manager(string userId, string name, Guid officeId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Email, $"{userId}@test.local"),
            new(OfficeClaims.OfficeId, officeId.ToString("D")),
            new(ClaimTypes.Role, PanelRoles.Manager)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}

using System.Security.Claims;
using Orbita.Contracts;

namespace Orbita.Tests;

internal static class TestPrincipalFactory
{
    public static ClaimsPrincipal Operator(string userId, string name, Guid officeId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Email, $"{userId}@test.local"),
            new(OfficeClaims.OfficeId, officeId.ToString("D")),
            new(ClaimTypes.Role, PanelRoles.Operator)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
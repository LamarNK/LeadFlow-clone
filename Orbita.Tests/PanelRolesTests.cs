using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class PanelRolesTests
{
    [Theory]
    [InlineData("Admin", "Admin")]
    [InlineData("admin", "Admin")]
    [InlineData("Operator", "Operator")]
    [InlineData("operator", "Operator")]
    [InlineData(null, "Operator")]
    [InlineData("", "Operator")]
    public void Normalize_ReturnsExpectedRole(string? input, string expected) =>
        Assert.Equal(expected, PanelRoles.Normalize(input));

    [Theory]
    [InlineData("admin", "Admin")]
    [InlineData("operator", "Operator")]
    [InlineData("unknown", "Operator")]
    public void RoleForProfileId_ReturnsExpectedRole(string profileId, string expected) =>
        Assert.Equal(expected, PanelRoles.RoleForProfileId(profileId));

    [Theory]
    [InlineData("Admin", "admin")]
    [InlineData("Operator", "operator")]
    public void ProfileIdForRole_ReturnsExpectedProfile(string role, string expected) =>
        Assert.Equal(expected, PanelRoles.ProfileIdForRole(role));
}
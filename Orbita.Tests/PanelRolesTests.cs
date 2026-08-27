using System.Security.Claims;
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

    [Theory]
    [InlineData(PanelRoles.Admin, true)]
    [InlineData(PanelRoles.OfficeLead, true)]
    [InlineData(PanelRoles.SeniorManager, false)]
    [InlineData(PanelRoles.Manager, false)]
    [InlineData(PanelRoles.Operator, false)]
    public void CanEditSuccessReport_AllowsOnlyAdminAndOfficeLead(string role, bool expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role)],
            authenticationType: "test"));

        Assert.Equal(expected, PanelRoles.CanEditSuccessReport(principal));
    }

    [Theory]
    [InlineData(PanelRoles.Admin, true)]
    [InlineData(PanelRoles.OfficeLead, true)]
    [InlineData(PanelRoles.SeniorManager, true)]
    [InlineData(PanelRoles.Manager, false)]
    [InlineData(PanelRoles.Operator, false)]
    public void CanDownloadSuccessReportArchive_AllowsOnlyElevatedManagement(string role, bool expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role)],
            authenticationType: "test"));

        Assert.Equal(expected, PanelRoles.CanDownloadSuccessReportArchive(principal));
    }
}

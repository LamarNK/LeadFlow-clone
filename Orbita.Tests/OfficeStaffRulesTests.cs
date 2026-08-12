using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class OfficeStaffRulesTests
{
    [Theory]
    [InlineData(PanelRoles.Admin, true)]
    [InlineData(PanelRoles.OfficeLead, true)]
    [InlineData(PanelRoles.SeniorManager, false)]
    [InlineData(PanelRoles.Manager, false)]
    [InlineData(PanelRoles.Operator, false)]
    public void CanManageStaff_OnlyAdminAndOfficeLead(string role, bool expected) =>
        Assert.Equal(expected, OfficeStaffRules.CanManageStaff(role));

    [Theory]
    [InlineData(PanelRoles.Manager, true)]
    [InlineData(PanelRoles.SeniorManager, true)]
    [InlineData(PanelRoles.OfficeLead, false)]
    [InlineData(PanelRoles.Admin, false)]
    [InlineData(PanelRoles.Operator, false)]
    public void IsAssignableRole_OnlyDeskLadder(string role, bool expected) =>
        Assert.Equal(expected, OfficeStaffRules.IsAssignableRole(role));

    [Fact]
    public void ValidateAssignableRole_RejectsOfficeLead()
    {
        var error = OfficeStaffRules.ValidateAssignableRole(PanelRoles.OfficeLead);
        Assert.NotNull(error);
        Assert.Contains("менеджера", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAssignableRole_AcceptsManager() =>
        Assert.Null(OfficeStaffRules.ValidateAssignableRole(PanelRoles.Manager));

    [Fact]
    public void ValidateManagedTarget_RejectsSelf()
    {
        var officeId = Guid.NewGuid();
        var error = OfficeStaffRules.ValidateManagedTarget(
            "user-1",
            "user-1",
            PanelRoles.Manager,
            officeId,
            officeId);

        Assert.NotNull(error);
        Assert.Contains("собственн", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateManagedTarget_RejectsOtherOffice()
    {
        var error = OfficeStaffRules.ValidateManagedTarget(
            "lead",
            "mgr",
            PanelRoles.Manager,
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.NotNull(error);
        Assert.Contains("другому офису", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateManagedTarget_RejectsOfficeLeadTarget()
    {
        var officeId = Guid.NewGuid();
        var error = OfficeStaffRules.ValidateManagedTarget(
            "lead",
            "other-lead",
            PanelRoles.OfficeLead,
            officeId,
            officeId);

        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateManagedTarget_AllowsManagerInSameOffice()
    {
        var officeId = Guid.NewGuid();
        Assert.Null(OfficeStaffRules.ValidateManagedTarget(
            "lead",
            "mgr",
            PanelRoles.Manager,
            officeId,
            officeId));
    }
}

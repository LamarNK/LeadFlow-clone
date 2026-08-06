using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class PanelPermissionsTests
{
    [Fact]
    public void DefaultForRole_ManagerGetsCrmBoardTasksAnalyticsAndPersonalSettings()
    {
        var permissions = PanelPermissions.DefaultForRole(PanelRoles.Manager);

        Assert.Equal(
            [PanelPermissions.CrmBoard, PanelPermissions.CrmTasks, PanelPermissions.CrmAnalytics, PanelPermissions.Settings],
            permissions);
    }

    [Fact]
    public void Normalize_RemovesUnknownAndDuplicatePermissions()
    {
        var permissions = PanelPermissions.Normalize(
        [
            PanelPermissions.CrmBoard,
            "unknown",
            PanelPermissions.CrmBoard,
            PanelPermissions.Settings
        ]);

        Assert.Equal([PanelPermissions.CrmBoard, PanelPermissions.Settings], permissions);
    }

    [Fact]
    public void Normalize_ExpandsLegacyCrmPermission()
    {
        var permissions = PanelPermissions.Normalize([PanelPermissions.Crm]);

        Assert.Equal(
            [PanelPermissions.CrmBoard, PanelPermissions.CrmTasks, PanelPermissions.CrmAnalytics],
            permissions);
    }

    [Fact]
    public void All_ContainsCrmAnalyticsPermissionForProfileManagement()
    {
        var permission = Assert.Single(PanelPermissions.All, x => x.Id == PanelPermissions.CrmAnalytics);

        Assert.Equal("CRM: Аналитика", permission.Label);
    }

    [Theory]
    [InlineData(PanelPermissions.CrmBoard, true)]
    [InlineData(PanelPermissions.Crm, true)]
    [InlineData(PanelPermissions.CrmAnalytics, false)]
    [InlineData(PanelPermissions.CrmTasks, false)]
    public void NeedsCrmAnalyticsUpgrade_PreservesExistingAnalyticsAccess(string permission, bool expected)
    {
        Assert.Equal(expected, PanelPermissions.NeedsCrmAnalyticsUpgrade([permission]));
    }
}

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

    [Fact]
    public void All_ContainsCrmTeamPermissionForProfileManagement()
    {
        var permission = Assert.Single(PanelPermissions.All, x => x.Id == PanelPermissions.CrmTeam);

        Assert.Equal("Команда CRM", permission.Label);
    }

    [Fact]
    public void DefaultForRole_AdminIncludesCrmTeam()
    {
        var permissions = PanelPermissions.DefaultForRole(PanelRoles.Admin);

        Assert.Contains(PanelPermissions.CrmTeam, permissions);
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

    [Fact]
    public void NeedsCrmTeamUpgrade_MatchesLegacyAdminTeamGate()
    {
        Assert.True(PanelPermissions.NeedsCrmTeamUpgrade(
        [
            PanelPermissions.Administration,
            PanelPermissions.CrmBoard,
            PanelPermissions.CrmTasks
        ]));
        Assert.False(PanelPermissions.NeedsCrmTeamUpgrade(
        [
            PanelPermissions.Administration,
            PanelPermissions.CrmBoard,
            PanelPermissions.CrmTasks,
            PanelPermissions.CrmTeam
        ]));
        Assert.False(PanelPermissions.NeedsCrmTeamUpgrade(
        [
            PanelPermissions.Administration
        ]));
        Assert.False(PanelPermissions.NeedsCrmTeamUpgrade(
        [
            PanelPermissions.CrmBoard,
            PanelPermissions.CrmTasks
        ]));
    }
}

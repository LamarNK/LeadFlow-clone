using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class PanelPermissionsTests
{
    [Fact]
    public void DefaultForRole_ManagerGetsCrmBoardTasksAndPersonalSettings()
    {
        var permissions = PanelPermissions.DefaultForRole(PanelRoles.Manager);

        Assert.Equal([PanelPermissions.CrmBoard, PanelPermissions.CrmTasks, PanelPermissions.Settings], permissions);
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

        Assert.Equal([PanelPermissions.CrmBoard, PanelPermissions.CrmTasks], permissions);
    }
}

using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class PanelPermissionsTests
{
    [Fact]
    public void DefaultForRole_ManagerGetsCrmAndPersonalSettings()
    {
        var permissions = PanelPermissions.DefaultForRole(PanelRoles.Manager);

        Assert.Equal([PanelPermissions.Crm, PanelPermissions.Settings], permissions);
    }

    [Fact]
    public void Normalize_RemovesUnknownAndDuplicatePermissions()
    {
        var permissions = PanelPermissions.Normalize(
        [
            PanelPermissions.Crm,
            "unknown",
            PanelPermissions.Crm,
            PanelPermissions.Settings
        ]);

        Assert.Equal([PanelPermissions.Crm, PanelPermissions.Settings], permissions);
    }
}

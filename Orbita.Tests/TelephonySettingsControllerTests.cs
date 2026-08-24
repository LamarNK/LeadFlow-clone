using Orbita.Web.Controllers;

namespace Orbita.Tests;

public sealed class TelephonySettingsControllerTests
{
    [Fact]
    public void ResolveAdminOfficeId_UsesOfficeSelectedInSwitcher_WhenUrlHasNoOfficeId()
    {
        var selectedOfficeId = Guid.NewGuid();

        var result = TelephonySettingsController.ResolveAdminOfficeId(
            Guid.Empty,
            selectedOfficeId,
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.Equal(selectedOfficeId, result);
    }

    [Fact]
    public void ResolveAdminOfficeId_UsesExplicitUrlOffice_ForDirectLink()
    {
        var requestedOfficeId = Guid.NewGuid();

        var result = TelephonySettingsController.ResolveAdminOfficeId(
            requestedOfficeId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.Equal(requestedOfficeId, result);
    }

    [Fact]
    public void ResolveAdminOfficeId_FallsBackToProfileOffice_WhenNothingSelected()
    {
        var profileOfficeId = Guid.NewGuid();

        var result = TelephonySettingsController.ResolveAdminOfficeId(
            Guid.Empty,
            null,
            profileOfficeId,
            Guid.NewGuid());

        Assert.Equal(profileOfficeId, result);
    }
}

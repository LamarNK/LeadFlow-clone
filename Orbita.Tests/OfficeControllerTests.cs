using Orbita.Web.Controllers;

namespace Orbita.Tests;

public sealed class OfficeControllerTests
{
    [Fact]
    public void SynchronizeOfficeIdQuery_ReplacesStaleOfficeAndPreservesOtherParameters()
    {
        var previousOfficeId = Guid.NewGuid();
        var selectedOfficeId = Guid.NewGuid();
        var target = $"/Settings/Telephony?officeId={previousOfficeId:D}&provider=beeline#lines";

        var result = OfficeController.SynchronizeOfficeIdQuery(target, selectedOfficeId);

        Assert.Equal(
            $"/Settings/Telephony?provider=beeline&officeId={selectedOfficeId:D}#lines",
            result);
    }

    [Fact]
    public void SynchronizeOfficeIdQuery_RemovesOfficeWhenAllOfficesSelected()
    {
        var target = $"/Settings/Telephony?officeId={Guid.NewGuid():D}&provider=sipout";

        var result = OfficeController.SynchronizeOfficeIdQuery(target, null);

        Assert.Equal("/Settings/Telephony?provider=sipout", result);
    }

    [Fact]
    public void SynchronizeOfficeIdQuery_LeavesUnscopedUrlUnchanged()
    {
        const string target = "/Dashboard?period=30d";

        var result = OfficeController.SynchronizeOfficeIdQuery(target, Guid.NewGuid());

        Assert.Equal(target, result);
    }
}

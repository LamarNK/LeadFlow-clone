using Orbita.Api.Data;

namespace Orbita.Tests;

public sealed class PanelUserProfileEntityTests
{
    [Fact]
    public void NewProfile_UsesThreeHundredCardCapacityByDefault()
    {
        var profile = new PanelUserProfileEntity();

        Assert.Equal(300, profile.CrmCapacity);
    }
}

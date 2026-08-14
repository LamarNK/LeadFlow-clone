using Orbita.Api.Data;

namespace Orbita.Tests;

public sealed class PanelUserProfileEntityTests
{
    [Fact]
    public void NewProfile_UsesHundredCardCapacityByDefault()
    {
        var profile = new PanelUserProfileEntity();

        Assert.Equal(100, profile.CrmCapacity);
    }
}

using LeadFlow.Core.Services.Captcha;

namespace Orbita.Tests;

public sealed class CaptchaCoordinateMapperTests
{
    [Theory]
    [InlineData(640, 400, 1280, 800, 640, 400)]
    [InlineData(0, 0, 1280, 800, 0, 0)]
    [InlineData(1280, 800, 1280, 800, 1280, 800)]
    public void MapToRemote_ScalesCoordinates(double panelX, double panelY, int viewportW, int viewportH, int expectedX, int expectedY)
    {
        var (x, y) = CaptchaCoordinateMapper.MapToRemote(panelX, panelY, 1280, 800, viewportW, viewportH);
        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
    }

    [Fact]
    public void MapToRemote_ClampsToViewport()
    {
        var (x, y) = CaptchaCoordinateMapper.MapToRemote(5000, 5000, 100, 100, 1280, 800);
        Assert.Equal(1279, x);
        Assert.Equal(799, y);
    }
}
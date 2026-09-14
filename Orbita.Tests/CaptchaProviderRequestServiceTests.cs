using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class CaptchaProviderRequestServiceTests
{
    [Fact]
    public void ToUtcBoundary_AlwaysReturnsUtcKind()
    {
        var local = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Unspecified);

        var result = CaptchaProviderRequestService.ToUtcBoundary(local, -300, DateTime.MinValue);
        var fallback = CaptchaProviderRequestService.ToUtcBoundary(null, -300, DateTime.MaxValue);

        Assert.Equal(new DateTime(2026, 9, 14, 5, 0, 0, DateTimeKind.Utc), result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(DateTimeKind.Utc, fallback.Kind);
        Assert.Equal(DateTime.MaxValue, fallback);
    }
}

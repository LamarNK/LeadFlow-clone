using Orbita.Api.Auth;

namespace Orbita.Tests;

public sealed class WorkerReleasePublishTokenValidatorTests
{
    [Fact]
    public void IsConfigured_requires_a_non_blank_token()
    {
        Assert.False(WorkerReleasePublishTokenValidator.IsConfigured(null));
        Assert.False(WorkerReleasePublishTokenValidator.IsConfigured("  "));
        Assert.True(WorkerReleasePublishTokenValidator.IsConfigured("publish-token"));
    }

    [Fact]
    public void IsValid_accepts_only_the_configured_token()
    {
        Assert.True(WorkerReleasePublishTokenValidator.IsValid("publish-token", "publish-token"));
        Assert.False(WorkerReleasePublishTokenValidator.IsValid("wrong-token", "publish-token"));
        Assert.False(WorkerReleasePublishTokenValidator.IsValid(null, "publish-token"));
        Assert.False(WorkerReleasePublishTokenValidator.IsValid("publish-token", null));
    }
}

using Microsoft.AspNetCore.Http;
using Orbita.Api.Auth;

namespace Orbita.Tests;

public sealed class WorkerHubPathsTests
{
    [Theory]
    [InlineData("/hubs/captcha")]
    [InlineData("/hubs/captcha/negotiate")]
    [InlineData("/hubs/browser-monitor")]
    [InlineData("/hubs/browser-monitor/negotiate")]
    [InlineData("/hubs/panel")]
    [InlineData("/hubs/worker")]
    public void IsWorkerHubPath_AcceptsKnownHubPrefixes(string path)
    {
        Assert.True(WorkerHubPaths.IsWorkerHubPath(new PathString(path)));
    }

    [Theory]
    [InlineData("/hubs/unknown")]
    [InlineData("/api/v1/workers")]
    [InlineData("/hubs/browser-monitors")]
    public void IsWorkerHubPath_RejectsUnknownPaths(string path)
    {
        Assert.False(WorkerHubPaths.IsWorkerHubPath(new PathString(path)));
    }

    [Fact]
    public void HubPrefixes_IncludesBrowserMonitor()
    {
        Assert.Contains("/hubs/browser-monitor", WorkerHubPaths.HubPrefixes);
    }
}
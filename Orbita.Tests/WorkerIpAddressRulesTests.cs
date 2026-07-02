using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerIpAddressRulesTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.31.229")]
    [InlineData("172.16.0.1")]
    public void IsUsablePublic_RejectsPrivateAndLoopback(string ip)
    {
        Assert.False(WorkerIpAddressRules.IsUsablePublic(ip));
    }

    [Theory]
    [InlineData("46.146.232.119")]
    [InlineData("194.58.57.251")]
    public void IsUsablePublic_AcceptsPublicIpv4(string ip)
    {
        Assert.True(WorkerIpAddressRules.IsUsablePublic(ip));
    }

    [Fact]
    public void ResolveForHeartbeat_PrefersReportedPublicIp_WhenConnectionIsLoopback()
    {
        var resolved = WorkerIpAddressRules.ResolveForHeartbeat("127.0.0.1", "46.146.232.119");

        Assert.Equal("46.146.232.119", resolved);
    }

    [Fact]
    public void ResolveForHeartbeat_PrefersConnectionIp_WhenBothArePublic()
    {
        var resolved = WorkerIpAddressRules.ResolveForHeartbeat("46.146.232.119", "194.58.57.251");

        Assert.Equal("46.146.232.119", resolved);
    }

    [Fact]
    public void ResolveForHeartbeat_ReturnsNull_WhenOnlyPrivateAddressesProvided()
    {
        var resolved = WorkerIpAddressRules.ResolveForHeartbeat("127.0.0.1", "192.168.31.229");

        Assert.Null(resolved);
    }
}
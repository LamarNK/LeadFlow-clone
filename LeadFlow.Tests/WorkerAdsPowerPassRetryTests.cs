using System;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Worker;
using PuppeteerSharp;
using Xunit;

namespace LeadFlow.Tests;

public sealed class WorkerAdsPowerPassRetryTests
{
    [Fact]
    public void CdpTimeout_GetsOneMinuteDelay()
    {
        var ex = AdsPowerCdpGuard.Timeout("тестовая операция", TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromMinutes(1), WorkerAdsPowerPassRetry.FromException(ex));
        Assert.Equal("Warning", WorkerAdsPowerPassRetry.EventType(ex));
    }

    [Fact]
    public void NetworkUnavailable_GetsNetworkDelay()
    {
        var ex = new AvitoNetworkUnavailableException(
            AvitoNetworkErrorKind.NoInternet,
            "ERR_INTERNET_DISCONNECTED",
            "https://www.avito.ru");

        Assert.Equal(TimeSpan.FromMinutes(5), WorkerAdsPowerPassRetry.FromException(ex));
    }

    [Fact]
    public void NetworkToken_WrappedInOuterException_IsStillDetected()
    {
        var navigation = new NavigationException("net::ERR_TIMED_OUT at https://www.avito.ru");
        var wrapped = new InvalidOperationException("переход на страницу откликов не выполнен", navigation);

        Assert.Equal(TimeSpan.FromMinutes(5), WorkerAdsPowerPassRetry.FromException(wrapped));
        Assert.Equal("Warning", WorkerAdsPowerPassRetry.EventType(wrapped));
    }

    [Fact]
    public void ProxyFailureException_CarriesNetworkDelay()
    {
        var ex = new AdsPowerProxyFailureException(
            "https://www.avito.ru",
            "net::ERR_PROXY_CONNECTION_FAILED — браузер не смог соединиться через прокси.");

        Assert.Equal(TimeSpan.FromMinutes(5), WorkerAdsPowerPassRetry.FromException(ex));
    }

    [Fact]
    public void UnrelatedException_GetsNoDelay()
    {
        Assert.Null(WorkerAdsPowerPassRetry.FromException(new InvalidOperationException("обычная ошибка")));
        Assert.Equal("Error", WorkerAdsPowerPassRetry.EventType(new InvalidOperationException("обычная ошибка")));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(4, 10)]
    [InlineData(9, 10)]
    public void Escalate_CdpBaseDelay_FollowsBackoffCurve(int consecutiveFailures, int expectedMinutes)
    {
        var escalated = WorkerAdsPowerPassRetry.Escalate(
            WorkerAdsPowerPassRetry.Delay,
            consecutiveFailures);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), escalated);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 5)]
    [InlineData(3, 5)]
    [InlineData(4, 10)]
    public void Escalate_NetworkBaseDelay_IsNeverShortened(int consecutiveFailures, int expectedMinutes)
    {
        var escalated = WorkerAdsPowerPassRetry.Escalate(
            WorkerAdsPowerPassRetry.NetworkDelay,
            consecutiveFailures);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), escalated);
    }

    [Fact]
    public void Escalate_ZeroBaseDelay_IsPreserved()
    {
        Assert.Equal(TimeSpan.Zero, WorkerAdsPowerPassRetry.Escalate(TimeSpan.Zero, 5));
    }
}
